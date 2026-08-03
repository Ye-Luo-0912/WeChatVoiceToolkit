using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WeChatVoice.Core.Errors;
using WeChatVoice.Core.Models;
using WeChatVoice.Core.Ports;

namespace WeChatVoice.Application;

/// <summary>
/// Private JSONL spool for large prepared selections. Only metadata is stored;
/// payloads remain owned by the verified catalog and are opened during export.
/// The path is deliberately confined to an application-owned temporary root.
/// </summary>
public static class PreparedSelectionSpool
{
    private const string DirectoryName = "prepared-selection";
    private const string FilePrefix = ".prepared-";
    private const int MaximumLineLength = 512 * 1024;
    private const int CopyBufferSize = 64 * 1024;
    private static readonly byte[] Newline = [0x0A];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>
    /// Above this count, the scan switches from an in-memory list to a disk
    /// spool. The threshold is intentionally conservative: normal Desktop
    /// contacts remain allocation-light while large histories avoid GC growth.
    /// </summary>
    public const int InMemoryRecordLimit = 4096;

    public static string RootDirectory
        => Path.Combine(Path.GetTempPath(), "WeChatVoiceToolkit", DirectoryName);

    internal static Task<PreparedSelectionSpoolWriter> CreateWriterAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = RootDirectory;
        Directory.CreateDirectory(root);
        EnsureNotReparsePoint(root);
        var path = Path.Combine(root, FilePrefix + Guid.NewGuid().ToString("N") + ".jsonl");
        return PreparedSelectionSpoolWriter.CreateAsync(path, cancellationToken);
    }

    public static async IAsyncEnumerable<VoiceRecord> ReadAsync(
        PreparedSelectionSpoolDescriptor descriptor,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ValidateDescriptorPath(descriptor);
        cancellationToken.ThrowIfCancellationRequested();

        var fileInfo = new FileInfo(descriptor.Path);
        if (!fileInfo.Exists
            || fileInfo.Length != descriptor.ByteLength)
        {
            throw new AppFailureException(
                ErrorCode.SelectionPlanMismatch,
                "The prepared voice selection spool is missing or changed.");
        }

        var actualHash = await ComputeSha256Async(descriptor.Path, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actualHash, descriptor.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new AppFailureException(
                ErrorCode.SelectionPlanMismatch,
                "The prepared voice selection spool integrity check failed.");
        }

        var count = 0;
        await using var stream = new FileStream(
            descriptor.Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
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
                throw new AppFailureException(
                    ErrorCode.SelectionPlanMismatch,
                    "The prepared voice selection spool contains an invalid record.");
            }

            VoiceRecord? record;
            try
            {
                var serialized = JsonSerializer.Deserialize<SpoolVoiceRecord>(line, JsonOptions);
                record = serialized?.ToVoiceRecord();
            }
            catch (JsonException exception)
            {
                throw new AppFailureException(
                    ErrorCode.SelectionPlanMismatch,
                    "The prepared voice selection spool contains invalid metadata.",
                    exception);
            }

            if (record is null)
            {
                throw new AppFailureException(
                    ErrorCode.SelectionPlanMismatch,
                    "The prepared voice selection spool contains an empty record.");
            }

            count++;
            yield return record;
        }

        if (count != descriptor.RecordCount)
        {
            throw new AppFailureException(
                ErrorCode.SelectionPlanMismatch,
                "The prepared voice selection spool record count changed.");
        }
    }

    public static async Task DeleteAsync(
        PreparedSelectionSpoolDescriptor descriptor,
        ITemporaryFileCleanupQueue? cleanupQueue = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ValidateDescriptorPath(descriptor);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (File.Exists(descriptor.Path))
            {
                File.Delete(descriptor.Path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            cleanupQueue?.Enqueue(
                descriptor.Path,
                new CleanupDiagnostic("prepared-selection-spool", "delete-failed", exception.GetType().Name));
        }

    }

    /// <summary>
    /// Removes only stale files from the known spool directory. It never
    /// recursively deletes the temporary root, and reparse points are refused.
    /// </summary>
    public static int CleanupOrphans(
        TimeSpan? olderThan = null,
        ITemporaryFileCleanupQueue? cleanupQueue = null)
    {
        var root = RootDirectory;
        if (!Directory.Exists(root))
        {
            return 0;
        }

        EnsureNotReparsePoint(root);
        var cutoff = DateTime.UtcNow - (olderThan ?? TimeSpan.FromHours(24));
        var removed = 0;
        foreach (var path in Directory.EnumerateFiles(root, FilePrefix + "*.jsonl", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0
                    || File.GetLastWriteTimeUtc(path) > cutoff)
                {
                    continue;
                }

                File.Delete(path);
                if (!File.Exists(path))
                {
                    removed++;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                cleanupQueue?.Enqueue(
                    path,
                    new CleanupDiagnostic("prepared-selection-spool", "orphan-delete-failed", exception.GetType().Name));
            }
        }

        return removed;
    }

    internal static void ValidateDescriptorPath(PreparedSelectionSpoolDescriptor descriptor)
    {
        if (!string.Equals(descriptor.FormatVersion, PreparedSelectionSpoolDescriptor.CurrentFormatVersion, StringComparison.Ordinal))
        {
            throw new AppFailureException(ErrorCode.SelectionPlanMismatch, "The prepared voice selection spool format is unsupported.");
        }

        var root = EnsureTrailingSeparator(Path.GetFullPath(RootDirectory));
        var path = Path.GetFullPath(descriptor.Path);
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(path).StartsWith(FilePrefix, StringComparison.Ordinal)
            || !Path.GetExtension(path).Equals(".jsonl", StringComparison.OrdinalIgnoreCase))
        {
            throw new AppFailureException(ErrorCode.SelectionPlanMismatch, "The prepared voice selection spool path is invalid.");
        }

        EnsureNotReparsePoint(Path.GetDirectoryName(path)!);
        if (File.Exists(path)
            && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new AppFailureException(ErrorCode.SelectionPlanMismatch, "The prepared voice selection spool path is invalid.");
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, CopyBufferSize), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                hash.AppendData(buffer, 0, read);
            }

            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static void EnsureNotReparsePoint(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("The prepared-selection spool directory cannot be a reparse point.");
        }
    }

    private static string EnsureTrailingSeparator(string path)
        => path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;

    internal sealed class PreparedSelectionSpoolWriter : IAsyncDisposable
    {
        private readonly string _path;
        private readonly FileStream _stream;
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private int _recordCount;
        private long _byteLength;
        private int _completed;

        private PreparedSelectionSpoolWriter(string path, FileStream stream)
        {
            _path = path;
            _stream = stream;
        }

        internal static Task<PreparedSelectionSpoolWriter> CreateAsync(
            string path,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return Task.FromResult(new PreparedSelectionSpoolWriter(path, stream));
        }

        internal async Task AppendAsync(VoiceRecord record, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(record);
            ObjectDisposedException.ThrowIf(_completed != 0, this);
            var json = JsonSerializer.SerializeToUtf8Bytes(SpoolVoiceRecord.From(record), JsonOptions);
            if (json.Length > MaximumLineLength)
            {
                throw new InvalidDataException("The prepared voice selection record is too large.");
            }

            await _stream.WriteAsync(json, cancellationToken).ConfigureAwait(false);
            await _stream.WriteAsync(Newline, cancellationToken).ConfigureAwait(false);
            _hash.AppendData(json);
            _hash.AppendData(Newline);
            _byteLength = checked(_byteLength + json.Length + 1);
            _recordCount++;
        }

        internal async Task<PreparedSelectionSpoolDescriptor> CompleteAsync(CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _completed) != 0)
            {
                throw new InvalidOperationException("The prepared-selection spool is already complete.");
            }

            try
            {
                await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                _stream.Flush(flushToDisk: true);
                var sha256 = Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();
                await _stream.DisposeAsync().ConfigureAwait(false);
                _hash.Dispose();
                Interlocked.Exchange(ref _completed, 1);
                return new PreparedSelectionSpoolDescriptor(
                    _path,
                    _recordCount,
                    _byteLength,
                    sha256);
            }
            catch
            {
                _hash.Dispose();
                await _stream.DisposeAsync().ConfigureAwait(false);
                Interlocked.Exchange(ref _completed, 1);
                throw;
            }
        }

        internal async Task AbortAsync(ITemporaryFileCleanupQueue? cleanupQueue, CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _completed, 1) == 0)
            {
                _hash.Dispose();
                await _stream.DisposeAsync().ConfigureAwait(false);
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(_path))
                {
                    File.Delete(_path);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                cleanupQueue?.Enqueue(
                    _path,
                    new CleanupDiagnostic("prepared-selection-spool", "abort-delete-failed", exception.GetType().Name));
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _completed, 1) == 0)
            {
                _hash.Dispose();
                await _stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    // VoiceRecord exposes computed SourceStableKey/Provenance properties and
    // therefore cannot be used directly as a parameterized JSON constructor.
    // Keep this private DTO aligned with the verified model constructor so the
    // spool format never gains derived or presentation-only fields.
    private sealed record SpoolVoiceRecord(
        string MessageId,
        string ConversationId,
        DateTimeOffset OccurredAtUtc,
        VoiceDirection Direction,
        VoicePayloadLocator? PayloadLocator,
        string? SourceDatabase,
        int? ShardNumber,
        string? ShardId,
        string? SnapshotId,
        string? AdapterId,
        string? AccountId,
        string? SourceMessageKey,
        string? PayloadSha256,
        long? PayloadByteLength,
        long? DurationMs,
        bool MediaLinked,
        string? SpeakerId,
        string? DataSetId,
        string? AdapterVersion,
        IReadOnlyList<string> DatabaseFingerprints,
        string? AdapterFamily,
        string? AccountStableId,
        string? ConversationStableId,
        string? MessagePrimaryKey,
        string? MediaPrimaryKey,
        string? DecodedSha256,
        long? DecodedByteLength,
        VoicePayloadState PayloadState)
    {
        internal static SpoolVoiceRecord From(VoiceRecord record)
            => new(
                record.MessageId,
                record.ConversationId,
                record.OccurredAtUtc,
                record.Direction,
                record.PayloadLocator,
                record.SourceDatabase,
                record.ShardNumber,
                record.ShardId,
                record.SnapshotId,
                record.AdapterId,
                record.AccountId,
                record.SourceMessageKey,
                record.PayloadSha256,
                record.PayloadByteLength,
                record.DurationMs,
                record.MediaLinked,
                record.SpeakerId,
                record.DataSetId,
                record.AdapterVersion,
                record.DatabaseFingerprints,
                record.AdapterFamily,
                record.AccountStableId,
                record.ConversationStableId,
                record.MessagePrimaryKey,
                record.MediaPrimaryKey,
                record.DecodedSha256,
                record.DecodedByteLength,
                record.PayloadState);

        internal VoiceRecord ToVoiceRecord()
            => new(
                MessageId,
                ConversationId,
                OccurredAtUtc,
                Direction,
                PayloadLocator,
                SourceDatabase,
                ShardNumber,
                ShardId,
                SnapshotId,
                AdapterId,
                AccountId,
                SourceMessageKey,
                PayloadSha256,
                PayloadByteLength,
                DurationMs,
                MediaLinked,
                SpeakerId,
                DataSetId,
                AdapterVersion,
                DatabaseFingerprints,
                AdapterFamily,
                AccountStableId,
                ConversationStableId,
                MessagePrimaryKey,
                MediaPrimaryKey,
                DecodedSha256,
                DecodedByteLength,
                PayloadState);
    }
}
