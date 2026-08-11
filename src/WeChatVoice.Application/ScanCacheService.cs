using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WeChatVoice.Core.Models;

namespace WeChatVoice.Application;

/// <summary>
/// A persistent, read-behind cache of an authoritative voice scan result. The
/// cache is keyed by the verified Workspace identity plus the query fingerprint
/// (which already binds the catalog context, contact, query, selection engine
/// version and duration resolver version). Only one entry exists per key, and
/// an entry is reused only when its own integrity verifies on read.
///
/// This is deliberately <em>not</em> a second authoritative database: it stores
/// only the metadata rows a verified catalog already produced, keyed by the
/// same fingerprint a fresh scan would yield, so a repeated scan of unchanged
/// data can be skipped. Payload bytes always remain owned by the catalog.
/// </summary>
public sealed class ScanCacheService
{
    private const string DirectoryName = "scan-cache";
    private const string ManifestFormatVersion = "scan-cache-v1";
    private const string DataExtension = ".jsonl";
    private const string ManifestExtension = ".cache.json";
    private const int MaximumLineLength = 512 * 1024;
    private const int CopyBufferSize = 64 * 1024;
    private static readonly byte[] Newline = [0x0A];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _rootDirectory;

    /// <summary>
    /// Creates the service rooted under <paramref name="appDataRoot"/>. The
    /// cache lives at <c>appDataRoot/Data/scan-cache</c> so it survives app
    /// restarts and is accounted for by storage inventory.
    /// </summary>
    public ScanCacheService(string appDataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataRoot);
        _rootDirectory = Path.Combine(appDataRoot, "Data", DirectoryName);
    }

    /// <summary>The application-owned cache root directory.</summary>
    public string RootDirectory => _rootDirectory;

    /// <summary>
    /// Persists a scan result for a verified Workspace under the query
    /// fingerprint. Records are written as the same JSONL representation the
    /// temporary spool uses, together with a sidecar manifest carrying the
    /// report and the stream descriptor. The data file is staged and moved
    /// into place before the manifest is committed.
    /// </summary>
    public async Task WriteAsync(
        string workspaceId,
        string queryFingerprint,
        VoiceScanReport report,
        IReadOnlyList<VoiceRecord>? records,
        PreparedSelectionSpoolDescriptor? spool,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ValidateQueryFingerprint(queryFingerprint);
        ArgumentNullException.ThrowIfNull(report);
        if (records is null && spool is null)
        {
            throw new ArgumentException("A scan cache entry requires records or a spool.", nameof(records));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.Combine(_rootDirectory, workspaceId);
        Directory.CreateDirectory(directory);
        EnsureNotReparsePoint(directory);
        var dataPath = Path.Combine(directory, queryFingerprint + DataExtension);
        var manifestPath = Path.Combine(directory, queryFingerprint + ManifestExtension);
        var tempDataPath = dataPath + ".tmp-" + Guid.NewGuid().ToString("N");

        long byteLength;
        int recordCount;
        string sha256;
        try
        {
            (byteLength, recordCount, sha256) = spool is not null
                ? await CopySpoolAsync(spool, tempDataPath, cancellationToken).ConfigureAwait(false)
                : await WriteRecordsAsync(records!, tempDataPath, cancellationToken).ConfigureAwait(false);
            File.Move(tempDataPath, dataPath, overwrite: true);
        }
        catch
        {
            TryDelete(tempDataPath);
            throw;
        }

        var manifest = new ScanCacheManifest(
            ManifestFormatVersion,
            queryFingerprint,
            workspaceId,
            recordCount,
            byteLength,
            sha256,
            ScanCacheReportDto.From(report),
            DateTimeOffset.UtcNow);
        await WriteManifestAsync(manifestPath, manifest, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads a previously persisted scan result for a verified Workspace.
    /// Returns <c>null</c> when there is no entry or the entry fails integrity
    /// verification (a corrupt entry is deleted so the next host recomputes).
    /// The returned records are the exact immutable rows a fresh scan would
    /// have produced for the same fingerprint.
    /// </summary>
    public async Task<ScanCacheReadResult?> TryReadAsync(
        string workspaceId,
        string queryFingerprint,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ValidateQueryFingerprint(queryFingerprint);
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.Combine(_rootDirectory, workspaceId);
        var dataPath = Path.Combine(directory, queryFingerprint + DataExtension);
        var manifestPath = Path.Combine(directory, queryFingerprint + ManifestExtension);
        if (!File.Exists(dataPath) || !File.Exists(manifestPath) || IsReparsePoint(directory))
        {
            return null;
        }

        ScanCacheManifest? manifest;
        try
        {
            await using var stream = new FileStream(
                manifestPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            manifest = await JsonSerializer.DeserializeAsync<ScanCacheManifest>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return null;
        }

        if (manifest is null
            || !string.Equals(manifest.FormatVersion, ManifestFormatVersion, StringComparison.Ordinal)
            || !string.Equals(manifest.QueryFingerprint, queryFingerprint, StringComparison.Ordinal)
            || !string.Equals(manifest.WorkspaceId, workspaceId, StringComparison.Ordinal)
            || manifest.RecordCount < 0
            || manifest.ByteLength < 0
            || string.IsNullOrWhiteSpace(manifest.Sha256)
            || manifest.Sha256.Length != 64
            || !manifest.Sha256.All(Uri.IsHexDigit))
        {
            return null;
        }

        try
        {
            return await ReadVerifiedAsync(directory, dataPath, manifest, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            TryDelete(dataPath);
            TryDelete(manifestPath);
            return null;
        }
        catch (Core.Errors.AppFailureException)
        {
            TryDelete(dataPath);
            TryDelete(manifestPath);
            return null;
        }
        catch (JsonException)
        {
            TryDelete(dataPath);
            TryDelete(manifestPath);
            return null;
        }
    }

    /// <summary>
    /// Removes a single cache entry. Deleting a scan cache entry never touches
    /// payloads; it only forces a later re-scan of the same fingerprint.
    /// </summary>
    public void DeleteAsync(string workspaceId, string queryFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ValidateQueryFingerprint(queryFingerprint);
        var directory = Path.Combine(_rootDirectory, workspaceId);
        TryDelete(Path.Combine(directory, queryFingerprint + DataExtension));
        TryDelete(Path.Combine(directory, queryFingerprint + ManifestExtension));
    }

    private async Task<ScanCacheReadResult> ReadVerifiedAsync(
        string directory,
        string dataPath,
        ScanCacheManifest manifest,
        CancellationToken cancellationToken)
    {
        // Small selections are materialized into the managed list; larger ones
        // are re-spooled into the temporary root so export keeps a bounded heap.
        var useSpool = manifest.RecordCount > PreparedSelectionSpool.InMemoryRecordLimit;
        var records = useSpool ? null : new List<VoiceRecord>(Math.Min(manifest.RecordCount, PreparedSelectionSpool.InMemoryRecordLimit));
        PreparedSelectionSpool.PreparedSelectionSpoolWriter? spoolWriter = useSpool
            ? await PreparedSelectionSpool.CreateWriterAsync(cancellationToken).ConfigureAwait(false)
            : null;
        PreparedSelectionSpoolDescriptor? spool = null;
        var count = 0;
        long byteLength = 0;
        try
        {
            await using var stream = new FileStream(
                dataPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var reader = new StreamReader(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                detectEncodingFromByteOrderMarks: false,
                bufferSize: CopyBufferSize,
                leaveOpen: false);
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (line.Length == 0 || line.Length > MaximumLineLength)
                {
                    throw new InvalidDataException("The scan cache contains an invalid record.");
                }

                var jsonBytes = Encoding.UTF8.GetBytes(line + "\n");
                hash.AppendData(jsonBytes);
                byteLength += jsonBytes.Length;
                var record = JsonSerializer.Deserialize<PreparedSelectionSpool.SpoolVoiceRecord>(line, JsonOptions)?.ToVoiceRecord()
                    ?? throw new InvalidDataException("The scan cache contains an empty record.");
                count++;
                if (useSpool)
                {
                    await spoolWriter!.AppendAsync(record, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    records!.Add(record);
                }
            }

            var actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (count != manifest.RecordCount
                || byteLength != manifest.ByteLength
                || !string.Equals(actualHash, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The scan cache failed integrity verification.");
            }

            if (useSpool)
            {
                spool = await spoolWriter!.CompleteAsync(cancellationToken).ConfigureAwait(false);
                spoolWriter = null;
            }

            return new ScanCacheReadResult(
                manifest.Report.ToReport(),
                records is null
                    ? Array.Empty<VoiceRecord>()
                    : new System.Collections.ObjectModel.ReadOnlyCollection<VoiceRecord>(records.ToArray()),
                spool);
        }
        finally
        {
            if (spoolWriter is not null)
            {
                await spoolWriter.AbortAsync(cleanupQueue: null, CancellationToken.None).ConfigureAwait(false);
            }

            _ = directory;
        }
    }

    private static async Task<(long ByteLength, int RecordCount, string Sha256)> WriteRecordsAsync(
        IReadOnlyList<VoiceRecord> records,
        string tempPath,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            tempPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var count = 0;
        long byteLength = 0;
        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var json = JsonSerializer.SerializeToUtf8Bytes(PreparedSelectionSpool.SpoolVoiceRecord.From(record), JsonOptions);
            if (json.Length > MaximumLineLength)
            {
                throw new InvalidDataException("The scan cache record is too large.");
            }

            await stream.WriteAsync(json, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(Newline, cancellationToken).ConfigureAwait(false);
            hash.AppendData(json);
            hash.AppendData(Newline);
            byteLength = checked(byteLength + json.Length + 1);
            count++;
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
        var sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        return (byteLength, count, sha256);
    }

    private static async Task<(long ByteLength, int RecordCount, string Sha256)> CopySpoolAsync(
        PreparedSelectionSpoolDescriptor spool,
        string tempPath,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            spool.Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(
            tempPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[CopyBufferSize];
        long byteLength = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            hash.AppendData(buffer, 0, read);
            byteLength += read;
        }

        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        destination.Flush(flushToDisk: true);
        var sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        // The source spool already carries verified RecordCount/ByteLength; the
        // copy is byte-identical, so those remain authoritative for the cache.
        return (spool.ByteLength, spool.RecordCount, sha256);
    }

    private static async Task WriteManifestAsync(
        string manifestPath,
        ScanCacheManifest manifest,
        CancellationToken cancellationToken)
    {
        var tempPath = manifestPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                CopyBufferSize,
                FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, manifest, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, manifestPath, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private static void ValidateQueryFingerprint(string queryFingerprint)
    {
        if (string.IsNullOrWhiteSpace(queryFingerprint)
            || queryFingerprint.Length != 64
            || !queryFingerprint.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("The scan cache key must be a SHA-256 query fingerprint.", nameof(queryFingerprint));
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static void EnsureNotReparsePoint(string path)
    {
        if (IsReparsePoint(path))
        {
            throw new IOException("The scan cache directory cannot be a reparse point.");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A failed best-effort delete is intentionally ignored; it only
            // delays rather than corrupts a later re-scan.
        }
    }

    private sealed record ScanCacheManifest(
        string FormatVersion,
        string QueryFingerprint,
        string WorkspaceId,
        int RecordCount,
        long ByteLength,
        string Sha256,
        ScanCacheReportDto Report,
        DateTimeOffset WrittenAtUtc);
}

/// <summary>
/// A JSON-friendly projection of <see cref="VoiceScanReport"/> used only inside
/// the scan cache manifest. The report itself exposes computed read-only
/// properties (<c>RejectedVoiceCount</c>, <c>DurationUnknownCount</c>) that
/// System.Text.Json cannot bind to a constructor, so the cache stores this DTO
/// and reconstructs the report on read.
/// </summary>
internal sealed record ScanCacheReportDto(
    int MatchedVoiceCount,
    long TotalDurationMs,
    DateTimeOffset? EarliestOccurredAtUtc,
    DateTimeOffset? LatestOccurredAtUtc,
    IReadOnlyDictionary<string, int> ShardCounts,
    int UnassociatedMediaCount,
    int EmptyBlobCount,
    int SuspectedDuplicateCount,
    int InvalidHeaderCount,
    int AmbiguousPayloadCount,
    IReadOnlyDictionary<string, int>? PayloadStateCounts,
    bool DeepScan,
    int? ExportableVoiceCount,
    long TotalPayloadBytes,
    int DurationKnownCount,
    string? ResultSetFingerprint)
{
    public static ScanCacheReportDto From(VoiceScanReport report)
        => new(
            report.MatchedVoiceCount,
            report.TotalDurationMs,
            report.EarliestOccurredAtUtc,
            report.LatestOccurredAtUtc,
            report.ShardCounts,
            report.UnassociatedMediaCount,
            report.EmptyBlobCount,
            report.SuspectedDuplicateCount,
            report.InvalidHeaderCount,
            report.AmbiguousPayloadCount,
            report.PayloadStateCounts,
            report.DeepScan,
            report.ExportableVoiceCount,
            report.TotalPayloadBytes,
            report.DurationKnownCount,
            report.ResultSetFingerprint);

    public VoiceScanReport ToReport()
        => new(
            MatchedVoiceCount,
            TotalDurationMs,
            EarliestOccurredAtUtc,
            LatestOccurredAtUtc,
            ShardCounts,
            UnassociatedMediaCount,
            EmptyBlobCount,
            SuspectedDuplicateCount,
            InvalidHeaderCount,
            AmbiguousPayloadCount,
            PayloadStateCounts,
            DeepScan,
            ExportableVoiceCount,
            TotalPayloadBytes,
            DurationKnownCount,
            ResultSetFingerprint);
}

/// <summary>
/// The rehydrated output of a scan cache hit. Records are the exact immutable
/// rows a fresh scan would produce; a spool is present only when the result was
/// large enough to require disk-backed prepared selection storage.
/// </summary>
public sealed record ScanCacheReadResult(
    VoiceScanReport Report,
    IReadOnlyList<VoiceRecord> Records,
    PreparedSelectionSpoolDescriptor? Spool);