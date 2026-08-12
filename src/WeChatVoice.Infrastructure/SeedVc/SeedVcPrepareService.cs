using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WeChatVoice.Core.Errors;
using WeChatVoice.Core.Models;
using WeChatVoice.Infrastructure.Serialization;

namespace WeChatVoice.Infrastructure.SeedVc;

/// <summary>
/// Converts a verified DatasetBuild output into the small, reproducible audio
/// directory expected by Seed-VC. This service is intentionally independent of
/// Python and is the only place where source files are segmented or downmixed.
/// </summary>
public sealed class SeedVcPrepareService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
    private static readonly string[] AudioExtensions = [".wav", ".flac", ".mp3", ".m4a", ".opus", ".ogg"];

    public async Task<SeedVcPrepareResult> PrepareAsync(
        SeedVcPrepareRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var datasetRoot = Path.GetFullPath(request.DatasetDirectory);
        if (!Directory.Exists(datasetRoot))
        {
            throw new AppFailureException(ErrorCode.InvalidRequest, "The dataset directory does not exist.");
        }

        var profile = request.Profile ?? new SeedVcPrepareProfile();
        var buildManifestPath = Path.Combine(datasetRoot, "build-manifest.json");
        var build = await ReadAsync<DatasetBuildManifest>(buildManifestPath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(build.LinkMode.ToString(), nameof(DatasetLinkMode.VerifiedCopy), StringComparison.Ordinal)
            && build.LinkMode != DatasetLinkMode.LinkedView)
        {
            throw new AppFailureException(ErrorCode.InvalidRequest, "The dataset build mode is not supported for Seed-VC preparation.");
        }

        var datasetFingerprint = string.IsNullOrWhiteSpace(build.BuildFingerprint)
            ? build.SelectionFingerprint
            : build.BuildFingerprint;
        var anchorFiles = await EnumerateAnchorsAsync(request.AnchorDirectory, cancellationToken).ConfigureAwait(false);
        var anchorHashes = new List<(string Path, string Hash)>();
        foreach (var file in anchorFiles)
        {
            anchorHashes.Add((file, await FileHashing.ComputeSha256Async(file, cancellationToken).ConfigureAwait(false)));
        }

        var prepFingerprint = ComputePrepFingerprint(datasetFingerprint, profile, anchorHashes.Select(static pair => pair.Hash));
        // Keep derived preparation data outside the verified Dataset build.
        // This makes the dataset immutable and allows the same preparation to
        // be reused after a Desktop restart without asking the user for a
        // second path.
        var outputRoot = Path.GetFullPath(request.OutputDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WeChatVoiceToolkit", "SeedVcPrep", prepFingerprint));
        EnsureNotInsideDataset(datasetRoot, outputRoot);
        var manifestPath = Path.Combine(outputRoot, "manifests", "prep-manifest.json");
        var journalPath = Path.Combine(outputRoot, "manifests", "sources.jsonl");
        if (Directory.Exists(outputRoot))
        {
            var reused = await TryReuseAsync(outputRoot, manifestPath, prepFingerprint, cancellationToken).ConfigureAwait(false);
            if (reused is not null)
            {
                return reused with { Reused = true };
            }

            throw new AppFailureException(ErrorCode.InvalidRequest, "The Seed-VC preparation output exists but failed verification.");
        }

        var parent = Path.GetDirectoryName(outputRoot) ?? throw new InvalidDataException("The Seed-VC preparation output has no parent directory.");
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, "." + Path.GetFileName(outputRoot) + ".staging-" + Guid.NewGuid().ToString("N"));
        var items = new List<SeedVcPrepareItem>();
        try
        {
            Directory.CreateDirectory(Path.Combine(staging, "audio"));
            Directory.CreateDirectory(Path.Combine(staging, "manifests"));
            foreach (var source in build.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = CombineUnderRoot(datasetRoot, source.RelativeAudioPath);
                var sourceHash = await VerifySourceAsync(path, source, cancellationToken).ConfigureAwait(false);
                await ProcessWavAsync(
                    path,
                    SeedVcSourceType.WeChat,
                    sourceHash,
                    source.ItemId,
                    copyIndex: 0,
                    staging,
                    profile,
                    items,
                    cancellationToken).ConfigureAwait(false);
            }

            for (var anchorIndex = 0; anchorIndex < anchorHashes.Count; anchorIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (path, hash) = anchorHashes[anchorIndex];
                for (var copy = 0; copy < profile.AnchorWeight; copy++)
                {
                    await ProcessWavAsync(
                        path,
                        SeedVcSourceType.Phone,
                        hash,
                        sourceItemId: null,
                        copy,
                        staging,
                        profile,
                        items,
                        cancellationToken).ConfigureAwait(false);
                }
            }

            var kept = items.Count(static item => item.State == SeedVcPrepareItemState.Kept);
            var rejected = items.Count - kept;
            var totalDuration = items.Where(static item => item.State == SeedVcPrepareItemState.Kept).Sum(static item => item.DurationMs ?? 0);
            var totalBytes = items.Where(static item => item.State == SeedVcPrepareItemState.Kept).Sum(static item => item.ByteLength);
            var manifest = new SeedVcPrepareManifest(
                prepFingerprint,
                datasetFingerprint,
                profile,
                DateTimeOffset.UtcNow,
                items,
                kept,
                rejected,
                totalDuration,
                totalBytes);
            await AtomicFileWriter.WriteJsonAsync(Path.Combine(staging, "manifests", "prep-manifest.json"), manifest, JsonOptions, cancellationToken).ConfigureAwait(false);
            await WriteJournalAsync(Path.Combine(staging, "manifests", "sources.jsonl"), items, cancellationToken).ConfigureAwait(false);
            Directory.Move(staging, outputRoot);
            return new SeedVcPrepareResult(outputRoot, manifestPath, journalPath, prepFingerprint, datasetFingerprint, kept, rejected, totalDuration, totalBytes, Reused: false);
        }
        finally
        {
            TryDelete(staging);
        }
    }

    private static async Task ProcessWavAsync(
        string sourcePath,
        SeedVcSourceType sourceType,
        string sourceHash,
        string? sourceItemId,
        int copyIndex,
        string stagingRoot,
        SeedVcPrepareProfile profile,
        List<SeedVcPrepareItem> items,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(Path.GetExtension(sourcePath), ".wav", StringComparison.OrdinalIgnoreCase))
        {
            items.Add(new SeedVcPrepareItem(sourceType, sourceHash, sourceItemId, 0, copyIndex, null, null, 0, null, SeedVcPrepareItemState.Rejected, "unsupported-format"));
            return;
        }

        PcmWave? wave;
        try
        {
            wave = await PcmWave.ReadAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or OverflowException)
        {
            items.Add(new SeedVcPrepareItem(sourceType, sourceHash, sourceItemId, 0, copyIndex, null, null, 0, null, SeedVcPrepareItemState.Rejected, "invalid-wav"));
            return;
        }

        if (wave.DurationMs < profile.MinimumDurationMs)
        {
            items.Add(new SeedVcPrepareItem(sourceType, sourceHash, sourceItemId, 0, copyIndex, null, null, wave.Data.Length, wave.DurationMs, SeedVcPrepareItemState.Rejected, "shorter-than-one-second"));
            return;
        }

        var maxFrames = Math.Max(1L, (long)Math.Floor(wave.SampleRate * profile.MaximumSeconds));
        var targetFrames = Math.Max(1L, (long)Math.Floor(wave.SampleRate * profile.TargetChunkSeconds));
        var minFrames = Math.Max(1L, (long)Math.Ceiling(wave.SampleRate * profile.MinimumSeconds));
        var segmentIndex = 0;
        for (long start = 0; start < wave.FrameCount;)
        {
            var remaining = wave.FrameCount - start;
            var frames = Math.Min(targetFrames, remaining);
            if (remaining > maxFrames && frames > maxFrames) frames = maxFrames;
            if (remaining - frames > 0 && remaining - frames < minFrames)
            {
                frames = remaining;
            }

            if (frames < minFrames || frames > maxFrames)
            {
                items.Add(new SeedVcPrepareItem(sourceType, sourceHash, sourceItemId, segmentIndex, copyIndex, null, null, 0, checked((long)(frames * 1000 / wave.SampleRate)), SeedVcPrepareItemState.Rejected, "segment-out-of-range"));
                break;
            }

            var sourcePrefix = sourceType == SeedVcSourceType.Phone ? "phone" : "wechat";
            var audioName = $"{sourcePrefix}-{segmentIndex:D6}-{sourceHash[..Math.Min(12, sourceHash.Length)]}-{copyIndex:D2}.wav";
            var relative = "audio/" + audioName;
            var destination = Path.Combine(stagingRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            var bytes = wave.WriteSegment(destination, start, frames, cancellationToken);
            var hash = await FileHashing.ComputeSha256Async(destination, cancellationToken).ConfigureAwait(false);
            var duration = checked((long)Math.Round(frames * 1000d / wave.SampleRate, MidpointRounding.AwayFromZero));
            items.Add(new SeedVcPrepareItem(sourceType, sourceHash, sourceItemId, segmentIndex, copyIndex, relative, hash, bytes, duration, SeedVcPrepareItemState.Kept));
            start += frames;
            segmentIndex++;
        }
    }

    private static async Task<string> VerifySourceAsync(string path, DatasetBuildItem source, CancellationToken cancellationToken)
    {
        var metadata = await FileHashing.ComputeMetadataAsync(path, cancellationToken).ConfigureAwait(false);
        if (metadata.ByteLength != source.ByteLength || !string.Equals(metadata.Sha256, source.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new AppFailureException(ErrorCode.InvalidRequest, "The dataset changed after verification; rebuild the dataset before preparing Seed-VC data.");
        }

        return metadata.Sha256;
    }

    private static async Task<IReadOnlyList<string>> EnumerateAnchorsAsync(string? directory, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(directory)) return Array.Empty<string>();
        var root = Path.GetFullPath(directory);
        if (!Directory.Exists(root)) throw new AppFailureException(ErrorCode.InvalidRequest, "The anchor directory does not exist.");
        var result = new List<string>();
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (AudioExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) result.Add(path);
        }

        return result;
    }

    private static async Task<SeedVcPrepareResult?> TryReuseAsync(string outputRoot, string manifestPath, string expectedFingerprint, CancellationToken cancellationToken)
    {
        if (!File.Exists(manifestPath)) return null;
        SeedVcPrepareManifest manifest;
        try { manifest = await ReadAsync<SeedVcPrepareManifest>(manifestPath, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException) { return null; }
        if (!string.Equals(manifest.PrepFingerprint, expectedFingerprint, StringComparison.OrdinalIgnoreCase)) return null;
        foreach (var item in manifest.Items.Where(static item => item.State == SeedVcPrepareItemState.Kept))
        {
            if (item.RelativeAudioPath is null || item.Sha256 is null) return null;
            var path = CombineUnderRoot(outputRoot, item.RelativeAudioPath);
            if (!File.Exists(path)) return null;
            var metadata = await FileHashing.ComputeMetadataAsync(path, cancellationToken).ConfigureAwait(false);
            if (metadata.ByteLength != item.ByteLength || !string.Equals(metadata.Sha256, item.Sha256, StringComparison.OrdinalIgnoreCase)) return null;
        }

        return new SeedVcPrepareResult(outputRoot, manifestPath, Path.Combine(outputRoot, "manifests", "sources.jsonl"), manifest.PrepFingerprint, manifest.DatasetBuildFingerprint, manifest.KeptCount, manifest.RejectedCount, manifest.TotalDurationMs, manifest.TotalByteLength, Reused: true);
    }

    private static string ComputePrepFingerprint(string datasetFingerprint, SeedVcPrepareProfile profile, IEnumerable<string> anchors)
    {
        var canonical = new StringBuilder(512);
        canonical.AppendLine("seedvc-prep-fingerprint-v1").AppendLine(datasetFingerprint).AppendLine(profile.Fingerprint);
        foreach (var anchor in anchors.Order(StringComparer.OrdinalIgnoreCase)) canonical.AppendLine(anchor.ToLowerInvariant());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();
    }

    private static async Task WriteJournalAsync(string path, IReadOnlyList<SeedVcPrepareItem> items, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        foreach (var item in items)
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(item, JsonOptions).AsMemory(), cancellationToken).ConfigureAwait(false);
        }
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<T> ReadAsync<T>(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) throw new AppFailureException(ErrorCode.InvalidRequest, $"Required dataset file is missing: {Path.GetFileName(path)}.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The dataset JSON document is empty.");
    }

    private static string CombineUnderRoot(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root);
        var path = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new AppFailureException(ErrorCode.InvalidRequest, "A dataset path escapes its root.");
        return path;
    }

    private static void EnsureNotInsideDataset(string datasetRoot, string outputRoot)
    {
        var dataset = Path.GetFullPath(datasetRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var output = Path.GetFullPath(outputRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (output.StartsWith(dataset, StringComparison.OrdinalIgnoreCase)) throw new AppFailureException(ErrorCode.InvalidRequest, "The Seed-VC preparation output must not be inside the source dataset.");
    }

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class PcmWave
    {
        private PcmWave(int sampleRate, short channels, byte[] data)
        {
            SampleRate = sampleRate;
            Channels = channels;
            Data = data;
        }

        public int SampleRate { get; }
        public short Channels { get; }
        public byte[] Data { get; }
        public int BytesPerSample => 2;
        public long FrameCount => Data.LongLength / (Channels * BytesPerSample);
        public long DurationMs => checked((long)Math.Round(FrameCount * 1000d / SampleRate, MidpointRounding.AwayFromZero));

        public static async Task<PcmWave> ReadAsync(string path, CancellationToken cancellationToken)
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            if (bytes.Length < 44 || !bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !bytes.AsSpan(8, 4).SequenceEqual("WAVE"u8)) throw new InvalidDataException("Not a RIFF/WAVE file.");
            var position = 12;
            ushort channels = 0;
            uint sampleRate = 0;
            ushort bits = 0;
            byte[]? data = null;
            while (position + 8 <= bytes.Length)
            {
                var id = bytes.AsSpan(position, 4);
                var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position + 4, 4));
                position += 8;
                if (size > bytes.Length - position) throw new InvalidDataException("WAV chunk exceeds file length.");
                if (id.SequenceEqual("fmt "u8) && size >= 16)
                {
                    var fmt = bytes.AsSpan(position, (int)size);
                    if (BinaryPrimitives.ReadUInt16LittleEndian(fmt) != 1) throw new InvalidDataException("WAV is not PCM.");
                    channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt[2..]);
                    sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(fmt[4..]);
                    bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt[14..]);
                }
                else if (id.SequenceEqual("data"u8)) data = bytes.AsSpan(position, (int)size).ToArray();
                position += checked((int)size);
                if ((size & 1) != 0) position++;
            }

            if (channels is < 1 or > 2 || sampleRate == 0 || bits is not (8 or 16 or 24 or 32) || data is null || data.Length == 0) throw new InvalidDataException("Unsupported PCM WAV.");
            var frameBytes = checked(channels * (bits / 8));
            var frames = data.Length / frameBytes;
            var mono = new byte[checked(frames * 2)];
            for (var frame = 0; frame < frames; frame++)
            {
                var offset = frame * frameBytes;
                var left = ReadPcmSample(data, offset, bits);
                var sample = channels == 1 ? left : (left + ReadPcmSample(data, offset + bits / 8, bits)) / 2;
                BinaryPrimitives.WriteInt16LittleEndian(mono.AsSpan(frame * 2, 2), (short)Math.Clamp(sample, short.MinValue, short.MaxValue));
            }
            // The output is always normalized to one channel, so subsequent
            // frame math must use the normalized layout rather than the source
            // channel count.
            return new PcmWave(checked((int)sampleRate), 1, mono);
        }

        public long WriteSegment(string path, long startFrame, long frames, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var data = Data.AsSpan(checked((int)(startFrame * 2)), checked((int)(frames * 2)));
            var riffSize = checked(36 + data.Length);
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024);
            Span<byte> header = stackalloc byte[44];
            "RIFF"u8.CopyTo(header);
            BinaryPrimitives.WriteUInt32LittleEndian(header[4..], checked((uint)riffSize));
            "WAVE"u8.CopyTo(header[8..]);
            "fmt "u8.CopyTo(header[12..]);
            BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 16);
            BinaryPrimitives.WriteUInt16LittleEndian(header[20..], 1);
            BinaryPrimitives.WriteUInt16LittleEndian(header[22..], 1);
            BinaryPrimitives.WriteUInt32LittleEndian(header[24..], checked((uint)SampleRate));
            BinaryPrimitives.WriteUInt32LittleEndian(header[28..], checked((uint)(SampleRate * 2)));
            BinaryPrimitives.WriteUInt16LittleEndian(header[32..], 2);
            BinaryPrimitives.WriteUInt16LittleEndian(header[34..], 16);
            "data"u8.CopyTo(header[36..]);
            BinaryPrimitives.WriteUInt32LittleEndian(header[40..], checked((uint)data.Length));
            stream.Write(header);
            stream.Write(data);
            cancellationToken.ThrowIfCancellationRequested();
            return 44L + data.Length;
        }

        private static int ReadPcmSample(byte[] data, int offset, int bits)
            => bits switch
            {
                8 => (data[offset] - 128) << 8,
                16 => BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset, 2)),
                24 => BinaryPrimitives.ReadInt32LittleEndian(new[] { data[offset], data[offset + 1], data[offset + 2], (byte)(data[offset + 2] >= 0x80 ? 0xff : 0x00) }) >> 8,
                32 => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4)) >> 16,
                _ => throw new InvalidDataException("Unsupported PCM bit depth."),
            };
    }
}
