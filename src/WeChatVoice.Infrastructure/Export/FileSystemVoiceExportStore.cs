using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WeChatVoice.Core.Models;
using WeChatVoice.Core.Ports;
using WeChatVoice.Infrastructure.Serialization;

namespace WeChatVoice.Infrastructure.Export;

/// <summary>
/// A local export store that owns stable source-key paths, independently
/// verifies original/decoded artifacts, and commits replacements atomically.
/// </summary>
public sealed class FileSystemVoiceExportStore : IVoiceExportStore
{
    private readonly ConcurrentDictionary<string, byte> _reservedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _exportRoot;

    public FileSystemVoiceExportStore(string exportRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exportRoot);
        _exportRoot = Path.GetFullPath(exportRoot);
    }

    public string ExportRoot => _exportRoot;

    public ValueTask<IExportRunJournal> BeginRunAsync(
        VoiceExportRunContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        var runsDirectory = ExportPathSafety.CombineUnderRoot(_exportRoot, "runs");
        Directory.CreateDirectory(runsDirectory);
        var journalPath = ExportPathSafety.CombineUnderRoot(_exportRoot, "runs", context.RunId + ".jsonl");
        var stream = new FileStream(
            journalPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return ValueTask.FromResult<IExportRunJournal>(new FileSystemExportRunJournal(stream));
    }

    public ValueTask<IExportItemLease> BeginItemAsync(
        VoiceRecord record,
        ExistingArtifactPolicy policy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();
        var sourceStableKey = record.SourceStableKey
            ?? throw new SourceIdentityRequiredException("The voice record lacks a complete SourceStableKey; reusable export is refused.");

        var occurredAtUtc = record.OccurredAtUtc.ToUniversalTime();
        var year = occurredAtUtc.ToString("yyyy", CultureInfo.InvariantCulture);
        var month = occurredAtUtc.ToString("MM", CultureInfo.InvariantCulture);
        var sourceId = ExportPathSafety.SanitizeFileStem(record.SourceMessageKey, "voice");
        var stableSuffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceStableKey)))
            .ToLowerInvariant()[..16];
        var fileName = $"{sourceId[..Math.Min(sourceId.Length, 64)]}-{stableSuffix}";
        var originalManifestPath = $"original/{year}/{month}/{fileName}.silk";
        var decodedManifestPath = $"decoded/{year}/{month}/{fileName}.wav";
        var originalPath = ExportPathSafety.CombineUnderRoot(_exportRoot, "original", year, month, $"{fileName}.silk");
        var decodedPath = ExportPathSafety.CombineUnderRoot(_exportRoot, "decoded", year, month, $"{fileName}.wav");

        var original = ReadExistingArtifact(originalPath, originalManifestPath);
        var decoded = ReadExistingArtifact(decodedPath, decodedManifestPath);
        var originalState = GetOriginalState(original, record);
        var decodedState = GetDecodedState(decoded, record);

        if (policy == ExistingArtifactPolicy.Fail
            && (originalState != ExportArtifactState.Missing || decodedState != ExportArtifactState.Missing))
        {
            throw new ExistingArtifactConflictException($"An original artifact already exists for source key '{sourceStableKey}'.");
        }

        if (policy is ExistingArtifactPolicy.SkipIfHashMatches or ExistingArtifactPolicy.VerifyOnly
            && originalState == ExportArtifactState.Conflict)
        {
            if (original is not null && string.IsNullOrWhiteSpace(record.PayloadSha256))
            {
                throw new ExistingArtifactNeedsHashException($"Existing artifact '{originalManifestPath}' cannot be safely reused because the source payload hash is unknown.");
            }
        }

        var replace = policy == ExistingArtifactPolicy.Replace;
        var reserveOriginal = replace || originalState == ExportArtifactState.Missing;
        var reserveDecoded = replace || decodedState == ExportArtifactState.Missing;
        var reserved = new List<string>(2);
        try
        {
            if (reserveOriginal)
            {
                Reserve(originalPath, sourceStableKey);
                reserved.Add(originalPath);
            }

            if (reserveDecoded)
            {
                Reserve(decodedPath, sourceStableKey);
                reserved.Add(decodedPath);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(originalPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(decodedPath)!);
            return ValueTask.FromResult<IExportItemLease>(new FileSystemExportItemLease(
                record,
                originalManifestPath,
                decodedManifestPath,
                originalPath,
                decodedPath,
                originalState,
                decodedState,
                original,
                decoded,
                replace,
                () => Release(reserved)));
        }
        catch
        {
            Release(reserved);
            throw;
        }
    }

    public Task FinalizeRunAsync(VoiceExportManifest manifest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var runsDirectory = ExportPathSafety.CombineUnderRoot(_exportRoot, "runs");
        Directory.CreateDirectory(runsDirectory);
        var journalPath = ExportPathSafety.CombineUnderRoot(_exportRoot, "runs", manifest.RunId + ".jsonl");
        var manifestPath = ExportPathSafety.CombineUnderRoot(_exportRoot, "runs", manifest.RunId + ".manifest.json");
        var latestPath = ExportPathSafety.CombineUnderRoot(_exportRoot, "latest.manifest.json");
        return FinalizeFilesAsync(manifest, journalPath, manifestPath, latestPath, cancellationToken);
    }

    private static async Task FinalizeFilesAsync(
        VoiceExportManifest manifest,
        string journalPath,
        string manifestPath,
        string latestPath,
        CancellationToken cancellationToken)
    {
        var journalManifest = await ReadManifestFromJournalAsync(manifest, journalPath, cancellationToken).ConfigureAwait(false);
        await AtomicFileWriter.WriteJsonAsync(manifestPath, journalManifest, InfrastructureJson.Indented, cancellationToken).ConfigureAwait(false);
        await AtomicFileWriter.WriteJsonAsync(latestPath, journalManifest, InfrastructureJson.Indented, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<VoiceExportManifest> ReadManifestFromJournalAsync(
        VoiceExportManifest fallback,
        string journalPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(journalPath))
        {
            return fallback;
        }

        var entries = new List<VoiceExportEntry>();
        var failures = new List<VoiceExportFailure>();
        await using var stream = new FileStream(journalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, 128 * 1024, leaveOpen: false);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var journalEvent = JsonSerializer.Deserialize<VoiceExportJournalEvent>(line, InfrastructureJson.Compact);
            if (journalEvent?.Entry is { } entry && journalEvent.Event is "item-committed" or "item-skipped")
            {
                entries.Add(entry);
            }

            if (journalEvent?.Failure is { } failure && journalEvent.Event == "item-failed")
            {
                failures.Add(failure);
            }
        }

        return new VoiceExportManifest(
            fallback.GeneratedAtUtc,
            entries.OrderBy(static entry => entry.OccurredAtUtc).ThenBy(static entry => entry.MessageId, StringComparer.Ordinal),
            failures.OrderBy(static failure => failure.MessageId ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static failure => failure.Stage, StringComparer.Ordinal)
                .ThenBy(static failure => failure.Error, StringComparer.Ordinal),
            fallback.RunId,
            fallback.SnapshotId,
            fallback.AdapterId,
            fallback.AccountId,
            fallback.DatasetId,
            fallback.AdapterVersion,
            fallback.DatabaseFingerprints);
    }

    private static ExportArtifact? ReadExistingArtifact(string path, string relativePath)
        => File.Exists(path)
            ? new ExportArtifact(relativePath, new FileInfo(path).Length, ComputeSha256(path))
            : null;

    private static ExportArtifactState GetOriginalState(ExportArtifact? artifact, VoiceRecord record)
    {
        if (artifact is null)
        {
            return ExportArtifactState.Missing;
        }

        var hashMatches = string.IsNullOrWhiteSpace(record.PayloadSha256)
            ? false
            : string.Equals(artifact.Sha256, record.PayloadSha256, StringComparison.OrdinalIgnoreCase);
        var lengthMatches = record.PayloadByteLength is null || artifact.ByteLength == record.PayloadByteLength.Value;
        return hashMatches && lengthMatches ? ExportArtifactState.VerifiedExisting : ExportArtifactState.Conflict;
    }

    private static ExportArtifactState GetDecodedState(ExportArtifact? artifact, VoiceRecord record)
    {
        if (artifact is null)
        {
            return ExportArtifactState.Missing;
        }

        var hashMatches = record.DecodedSha256 is null
            || string.Equals(artifact.Sha256, record.DecodedSha256, StringComparison.OrdinalIgnoreCase);
        var lengthMatches = record.DecodedByteLength is null || artifact.ByteLength == record.DecodedByteLength.Value;
        return hashMatches && lengthMatches ? ExportArtifactState.VerifiedExisting : ExportArtifactState.Conflict;
    }

    private void Reserve(string path, string sourceStableKey)
    {
        if (!_reservedPaths.TryAdd(path, 0))
        {
            throw new ExistingArtifactConflictException($"An export for source key '{sourceStableKey}' is already in progress.");
        }
    }

    private static string ComputeSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan);
        using var hasher = SHA256.Create();
        return Convert.ToHexString(hasher.ComputeHash(stream)).ToLowerInvariant();
    }

    private void Release(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            _reservedPaths.TryRemove(path, out _);
        }
    }

    private sealed class FileSystemExportItemLease : IExportItemLease
    {
        private readonly string _originalPath;
        private readonly string _decodedPath;
        private readonly bool _replace;
        private readonly Action _release;
        private string? _originalTemporaryPath;
        private string? _decodedTemporaryPath;
        private bool _originalCommitted;
        private bool _decodedCommitted;
        private bool _disposed;

        public FileSystemExportItemLease(
            VoiceRecord record,
            string originalManifestPath,
            string decodedManifestPath,
            string originalPath,
            string decodedPath,
            ExportArtifactState originalState,
            ExportArtifactState decodedState,
            ExportArtifact? existingOriginalArtifact,
            ExportArtifact? existingDecodedArtifact,
            bool replace,
            Action release)
        {
            Record = record;
            OriginalManifestPath = originalManifestPath;
            DecodedManifestPath = decodedManifestPath;
            _originalPath = originalPath;
            _decodedPath = decodedPath;
            OriginalState = originalState;
            DecodedState = decodedState;
            ExistingOriginalArtifact = existingOriginalArtifact;
            ExistingDecodedArtifact = existingDecodedArtifact;
            _replace = replace;
            _release = release;
        }

        public VoiceRecord Record { get; }
        public ExportArtifactState OriginalState { get; }
        public ExportArtifactState DecodedState { get; }
        public ExportArtifact? ExistingOriginalArtifact { get; }
        public ExportArtifact? ExistingDecodedArtifact { get; }
        public string OriginalManifestPath { get; }
        public string DecodedManifestPath { get; }

        public ValueTask<Stream> OpenOriginalWriteAsync(CancellationToken cancellationToken)
            => OpenWriteAsync(isDecoded: false, cancellationToken);

        public ValueTask<Stream> OpenOriginalReadAsync(CancellationToken cancellationToken)
        {
            EnsureUsable();
            if (!_originalCommitted && OriginalState == ExportArtifactState.Conflict)
            {
                throw new InvalidOperationException("The original artifact is not available for reading.");
            }

            return ValueTask.FromResult<Stream>(new FileStream(_originalPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan));
        }

        public Task<ExportArtifact> CommitOriginalAsync(CancellationToken cancellationToken)
            => CommitAsync(isDecoded: false, cancellationToken);

        public ValueTask<Stream> OpenDecodedWriteAsync(CancellationToken cancellationToken)
            => OpenWriteAsync(isDecoded: true, cancellationToken);

        public Task<ExportArtifact> CommitDecodedAsync(CancellationToken cancellationToken)
            => CommitAsync(isDecoded: true, cancellationToken);

        public Task RollbackAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeleteTemporary(ref _originalTemporaryPath);
            DeleteTemporary(ref _decodedTemporaryPath);
            return Task.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                await RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _release();
            }
        }

        private ValueTask<Stream> OpenWriteAsync(bool isDecoded, CancellationToken cancellationToken)
        {
            EnsureUsable();
            var state = isDecoded ? DecodedState : OriginalState;
            var committed = isDecoded ? _decodedCommitted : _originalCommitted;
            var temporary = isDecoded ? _decodedTemporaryPath : _originalTemporaryPath;
            if (!_replace && state == ExportArtifactState.VerifiedExisting)
            {
                throw new InvalidOperationException("The verified existing artifact does not need to be rewritten.");
            }

            if (state == ExportArtifactState.Conflict && !_replace)
            {
                throw new ExistingArtifactConflictException("The existing artifact conflicts with the source expectation.");
            }

            if (committed || temporary is not null)
            {
                throw new InvalidOperationException("The artifact has already been opened or committed.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var finalPath = isDecoded ? _decodedPath : _originalPath;
            var temporaryPath = Path.Combine(Path.GetDirectoryName(finalPath)!, $".{Path.GetFileName(finalPath)}.{Guid.NewGuid():N}.tmp");
            if (isDecoded)
            {
                _decodedTemporaryPath = temporaryPath;
            }
            else
            {
                _originalTemporaryPath = temporaryPath;
            }

            Stream stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return ValueTask.FromResult(stream);
        }

        private async Task<ExportArtifact> CommitAsync(bool isDecoded, CancellationToken cancellationToken)
        {
            EnsureUsable();
            cancellationToken.ThrowIfCancellationRequested();
            var temporaryPath = isDecoded ? _decodedTemporaryPath : _originalTemporaryPath;
            if (temporaryPath is null)
            {
                throw new InvalidOperationException("No temporary artifact has been opened.");
            }

            var manifestPath = isDecoded ? DecodedManifestPath : OriginalManifestPath;
            var artifact = await ComputeArtifactAsync(temporaryPath, manifestPath, cancellationToken).ConfigureAwait(false);
            var expectedHash = isDecoded ? Record.DecodedSha256 : Record.PayloadSha256;
            var expectedLength = isDecoded ? Record.DecodedByteLength : Record.PayloadByteLength;
            if ((expectedHash is not null && !string.Equals(expectedHash, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
                || (expectedLength is not null && expectedLength.Value != artifact.ByteLength))
            {
                DeleteTemporary(ref temporaryPath);
                if (isDecoded)
                {
                    _decodedTemporaryPath = null;
                }
                else
                {
                    _originalTemporaryPath = null;
                }

                throw new SourceContentMismatchException(isDecoded ? "decoded" : "original", expectedLength, artifact.ByteLength, expectedHash, artifact.Sha256);
            }

            var finalPath = isDecoded ? _decodedPath : _originalPath;
            AtomicCommit(temporaryPath, finalPath);
            if (isDecoded)
            {
                _decodedTemporaryPath = null;
                _decodedCommitted = true;
            }
            else
            {
                _originalTemporaryPath = null;
                _originalCommitted = true;
            }

            return artifact;
        }

        private static void AtomicCommit(string temporaryPath, string finalPath)
        {
            if (!File.Exists(finalPath))
            {
                File.Move(temporaryPath, finalPath);
                return;
            }

            var backupPath = finalPath + ".backup-" + Guid.NewGuid().ToString("N");
            try
            {
                File.Replace(temporaryPath, finalPath, backupPath, ignoreMetadataErrors: true);
            }
            finally
            {
                try
                {
                    if (File.Exists(backupPath))
                    {
                        File.Delete(backupPath);
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        private static async Task<ExportArtifact> ComputeArtifactAsync(string path, string relativePath, CancellationToken cancellationToken)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[128 * 1024];
            long length = 0;
            int count;
            while ((count = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
            {
                hash.AppendData(buffer, 0, count);
                length = checked(length + count);
            }

            return new ExportArtifact(relativePath, length, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
        }

        private void EnsureUsable()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(FileSystemExportItemLease));
            }
        }

        private static void DeleteTemporary(ref string? path)
        {
            if (path is null)
            {
                return;
            }

            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            path = null;
        }
    }

    private sealed class FileSystemExportRunJournal : IExportRunJournal
    {
        private readonly FileStream _stream;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private bool _disposed;

        public FileSystemExportRunJournal(FileStream stream) => _stream = stream;

        public async Task AppendAsync(VoiceExportJournalEvent journalEvent, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(journalEvent);
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(FileSystemExportRunJournal));
                }

                var line = JsonSerializer.Serialize(journalEvent, InfrastructureJson.Compact) + Environment.NewLine;
                var bytes = Encoding.UTF8.GetBytes(line);
                await _stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await _stream.DisposeAsync().ConfigureAwait(false);
            _gate.Dispose();
        }
    }
}
