using System.Collections.Concurrent;
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
    private readonly SemaphoreSlim _artifactIndexGate = new(1, 1);
    private Dictionary<string, ArtifactIndexEntry>? _artifactIndex;

    public FileSystemVoiceExportStore(string exportRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exportRoot);
        _exportRoot = Path.GetFullPath(exportRoot);
    }

    public string ExportRoot => _exportRoot;

    public ValueTask<IExportRunLease> BeginRunAsync(
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
        return ValueTask.FromResult<IExportRunLease>(new FileSystemExportRunJournal(_exportRoot, context.RunId, stream));
    }

    public async ValueTask<IExportItemLease> BeginItemAsync(
        VoiceRecord record,
        ExistingArtifactPolicy policy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();
        var sourceStableKey = record.SourceStableKey
            ?? throw new SourceIdentityRequiredException("The voice record lacks a complete SourceStableKey; reusable export is refused.");

        var stableKeyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceStableKey)))
            .ToLowerInvariant();
        var prefix1 = stableKeyHash[..2];
        var prefix2 = stableKeyHash[2..4];
        var fileName = stableKeyHash[..32];
        var originalManifestPath = $"original/{prefix1}/{prefix2}/{fileName}.silk";
        var decodedManifestPath = $"decoded/{prefix1}/{prefix2}/{fileName}.wav";
        var originalPath = ExportPathSafety.CombineUnderRoot(_exportRoot, "original", prefix1, prefix2, $"{fileName}.silk");
        var decodedPath = ExportPathSafety.CombineUnderRoot(_exportRoot, "decoded", prefix1, prefix2, $"{fileName}.wav");

        var original = await ReadExistingArtifactAsync(originalPath, originalManifestPath, cancellationToken).ConfigureAwait(false);
        var decoded = await ReadExistingArtifactAsync(decodedPath, decodedManifestPath, cancellationToken).ConfigureAwait(false);
        var originalState = GetOriginalState(original, record);
        var decodedState = await GetDecodedStateAsync(decoded, record, decodedPath, cancellationToken).ConfigureAwait(false);

        if (policy == ExistingArtifactPolicy.Fail
            && (originalState != ExportArtifactState.Missing || decodedState != ExportArtifactState.Missing))
        {
            throw new ExistingArtifactConflictException($"An original artifact already exists for source key '{sourceStableKey}'.");
        }

        if (policy == ExistingArtifactPolicy.VerifyOnly
            && originalState == ExportArtifactState.PendingExisting)
        {
            throw new ExistingArtifactNeedsHashException($"Existing artifact '{originalManifestPath}' cannot be verified because the source payload hash is unknown.");
        }

        var replace = policy == ExistingArtifactPolicy.Replace;
        var reserveOriginal = replace || originalState is ExportArtifactState.Missing or ExportArtifactState.PendingExisting;
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
            return new FileSystemExportItemLease(
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
                () => Release(reserved));
        }
        catch
        {
            Release(reserved);
            throw;
        }
    }

    public async Task<VoiceExportManifest> RecoverRunAsync(string journalPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journalPath);
        var fullJournalPath = Path.GetFullPath(journalPath);
        var runsDirectory = ExportPathSafety.CombineUnderRoot(_exportRoot, "runs");
        var expectedPrefix = runsDirectory.EndsWith(Path.DirectorySeparatorChar) ? runsDirectory : runsDirectory + Path.DirectorySeparatorChar;
        if (!fullJournalPath.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetDirectoryName(fullJournalPath), runsDirectory, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetExtension(fullJournalPath), ".jsonl", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The recovery journal must be a .jsonl file directly under the export root's runs directory.");
        }

        var runId = Path.GetFileNameWithoutExtension(fullJournalPath);
        var fallback = new VoiceExportManifest(DateTimeOffset.UtcNow, RunId: runId, RunStatus: ExportRunStatus.Failed);
        var recovered = await ReadManifestFromJournalAsync(fallback, fullJournalPath, cancellationToken).ConfigureAwait(false);
        var manifestPath = ExportPathSafety.CombineUnderRoot(_exportRoot, "runs", runId + ".manifest.json");
        var latestPath = ExportPathSafety.CombineUnderRoot(_exportRoot, "latest.manifest.json");
        await AtomicFileWriter.WriteJsonAsync(manifestPath, recovered, InfrastructureJson.Indented, cancellationToken).ConfigureAwait(false);
        await AtomicFileWriter.WriteJsonAsync(latestPath, recovered, InfrastructureJson.Indented, cancellationToken).ConfigureAwait(false);
        return recovered;
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
        VoiceCatalogContext? journalContext = null;
        var runStatus = fallback.RunStatus;
        await using var journalStream = new FileStream(journalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var journalReader = new StreamReader(journalStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, 128 * 1024, leaveOpen: false);
        var journalText = await journalReader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        var journalLines = journalText.Split('\n');
        for (var lineIndex = 0; lineIndex < journalLines.Length; lineIndex++)
        {
            var line = journalLines[lineIndex].TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            VoiceExportJournalEvent? journalEvent;
            try
            {
                journalEvent = JsonSerializer.Deserialize<VoiceExportJournalEvent>(line, InfrastructureJson.Compact);
            }
            catch (JsonException)
            {
                // A process crash can leave a truncated final JSONL line. A
                // malformed line in the middle is corruption and must not be
                // silently skipped.
                var isFinalTruncatedLine = lineIndex == journalLines.Length - 1 && !journalText.EndsWith('\n');
                if (!isFinalTruncatedLine)
                {
                    throw new InvalidDataException("The export Journal contains a malformed non-final JSONL line.");
                }

                break;
            }
            if (journalEvent is null)
            {
                throw new InvalidDataException("The export Journal contains a null event.");
            }

            if (!string.Equals(journalEvent.RunId, fallback.RunId, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"The export Journal event RunId '{journalEvent.RunId}' does not match '{fallback.RunId}'.");
            }

            if (journalEvent.Entry is { } entry && journalEvent.Event is "item-committed" or "item-skipped")
            {
                entries.Add(entry);
            }

            if (journalEvent.Context is { } context)
            {
                journalContext = context;
            }

            if (journalEvent.Failure is { } failure && journalEvent.Event == "item-failed")
            {
                failures.Add(failure);
            }

            runStatus = journalEvent.Event switch
            {
                "run-cancelled" => ExportRunStatus.Cancelled,
                "run-failed" => ExportRunStatus.Failed,
                "processing-completed" => failures.Count > 0 ? ExportRunStatus.CompletedWithFailures : ExportRunStatus.Completed,
                _ => runStatus,
            };
        }

        return new VoiceExportManifest(
            fallback.GeneratedAtUtc,
            entries.OrderBy(static entry => entry.OccurredAtUtc).ThenBy(static entry => entry.MessageId, StringComparer.Ordinal),
            failures.OrderBy(static failure => failure.MessageId ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static failure => failure.Stage, StringComparer.Ordinal)
                .ThenBy(static failure => failure.Error, StringComparer.Ordinal),
            fallback.RunId,
            journalContext?.SnapshotId ?? fallback.SnapshotId,
            journalContext?.AdapterId ?? fallback.AdapterId,
            journalContext?.AccountId ?? fallback.AccountId,
            journalContext?.DatasetId ?? fallback.DatasetId,
            journalContext?.AdapterVersion ?? fallback.AdapterVersion,
            journalContext?.DatabaseFingerprints ?? fallback.DatabaseFingerprints,
            runStatus,
            runStatus == ExportRunStatus.Cancelled,
            journalContext?.MaterializationProvenance ?? fallback.Provenance);
    }

    private async Task<ExportArtifact?> ReadExistingArtifactAsync(string path, string relativePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var info = new FileInfo(path);
        await _artifactIndexGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureArtifactIndexLoadedAsync(cancellationToken).ConfigureAwait(false);
            var lastWriteTicks = info.LastWriteTimeUtc.Ticks;
            var fileId = ArtifactFileIdentity.Read(path);
            if (_artifactIndex!.TryGetValue(relativePath, out var cached)
                && string.Equals(cached.FileId, fileId, StringComparison.Ordinal)
                && cached.Length == info.Length
                && cached.LastWriteUtcTicks == lastWriteTicks)
            {
                return new ExportArtifact(relativePath, cached.Length, cached.Sha256);
            }

            var sha256 = await FileHashing.ComputeSha256Async(path, cancellationToken).ConfigureAwait(false);
            var entry = new ArtifactIndexEntry(relativePath, fileId, info.Length, lastWriteTicks, sha256, DateTimeOffset.UtcNow);
            _artifactIndex[relativePath] = entry;
            var indexPath = Path.Combine(_exportRoot, "artifact-index.jsonl");
            Directory.CreateDirectory(_exportRoot);
            await File.AppendAllTextAsync(indexPath, JsonSerializer.Serialize(entry, InfrastructureJson.Compact) + Environment.NewLine, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            return new ExportArtifact(relativePath, info.Length, sha256);
        }
        finally
        {
            _artifactIndexGate.Release();
        }
    }

    private async Task EnsureArtifactIndexLoadedAsync(CancellationToken cancellationToken)
    {
        if (_artifactIndex is not null)
        {
            return;
        }

        _artifactIndex = new Dictionary<string, ArtifactIndexEntry>(StringComparer.OrdinalIgnoreCase);
        var indexPath = Path.Combine(_exportRoot, "artifact-index.jsonl");
        if (!File.Exists(indexPath))
        {
            return;
        }

        await using var stream = new FileStream(indexPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 32 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var entry = JsonSerializer.Deserialize<ArtifactIndexEntry>(line, InfrastructureJson.Compact);
                if (entry is not null && !string.IsNullOrWhiteSpace(entry.RelativePath))
                {
                    _artifactIndex[entry.RelativePath] = entry;
                }
            }
            catch (JsonException)
            {
                // A torn final index line is ignored; the next verification
                // will replace the entry with a complete record.
            }
        }
    }

    private sealed record ArtifactIndexEntry(
        string RelativePath,
        string? FileId,
        long Length,
        long LastWriteUtcTicks,
        string Sha256,
        DateTimeOffset LastVerifiedUtc);

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
        if (hashMatches && lengthMatches)
        {
            return ExportArtifactState.VerifiedExisting;
        }

        if (string.IsNullOrWhiteSpace(record.PayloadSha256) && lengthMatches)
        {
            // The source hash is unknown, so identity can only be decided at
            // commit time against the freshly computed source bytes.
            return ExportArtifactState.PendingExisting;
        }

        return ExportArtifactState.Conflict;
    }

    private static async Task<ExportArtifactState> GetDecodedStateAsync(ExportArtifact? artifact, VoiceRecord record, string path, CancellationToken cancellationToken)
    {
        if (artifact is null)
        {
            return ExportArtifactState.Missing;
        }

        var hasKnownHash = !string.IsNullOrWhiteSpace(record.DecodedSha256);
        var hashMatches = !hasKnownHash
            || string.Equals(artifact.Sha256, record.DecodedSha256, StringComparison.OrdinalIgnoreCase);
        var lengthMatches = record.DecodedByteLength is null || artifact.ByteLength == record.DecodedByteLength.Value;
        var wavValid = await WavFileValidator.IsValidAsync(path, cancellationToken).ConfigureAwait(false);
        return hashMatches && lengthMatches && (hasKnownHash || wavValid) ? ExportArtifactState.VerifiedExisting : ExportArtifactState.Conflict;
    }

    private void Reserve(string path, string sourceStableKey)
    {
        if (!_reservedPaths.TryAdd(path, 0))
        {
            throw new ExistingArtifactConflictException($"An export for source key '{sourceStableKey}' is already in progress.");
        }
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
        private ExportArtifactState _originalState;
        private ExportArtifactState _decodedState;
        private ExportArtifact? _existingDecodedArtifact;
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
            _originalState = originalState;
            _decodedState = decodedState;
            ExistingOriginalArtifact = existingOriginalArtifact;
            _existingDecodedArtifact = existingDecodedArtifact;
            _replace = replace;
            _release = release;
        }

        public VoiceRecord Record { get; }
        public ExportArtifactState OriginalState => _originalState;
        public ExportArtifactState DecodedState => _decodedState;
        public ExportArtifact? ExistingOriginalArtifact { get; }
        public ExportArtifact? ExistingDecodedArtifact => _existingDecodedArtifact;
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

        public Task<ExportArtifact> CommitOriginalAsync(ExportArtifact computedArtifact, CancellationToken cancellationToken)
            => CommitAsync(isDecoded: false, cancellationToken, computedArtifact);

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

        private async Task<ExportArtifact> CommitAsync(
            bool isDecoded,
            CancellationToken cancellationToken,
            ExportArtifact? computedArtifact = null)
        {
            EnsureUsable();
            cancellationToken.ThrowIfCancellationRequested();
            var temporaryPath = isDecoded ? _decodedTemporaryPath : _originalTemporaryPath;
            if (temporaryPath is null)
            {
                throw new InvalidOperationException("No temporary artifact has been opened.");
            }

            var manifestPath = isDecoded ? DecodedManifestPath : OriginalManifestPath;
            var artifact = computedArtifact is null
                ? await ComputeArtifactAsync(temporaryPath, manifestPath, cancellationToken).ConfigureAwait(false)
                : ValidateComputedArtifact(temporaryPath, computedArtifact, manifestPath);

            if (!isDecoded && !_replace && _originalState == ExportArtifactState.PendingExisting && ExistingOriginalArtifact is { } existing)
            {
                // The source hash was unknown when the lease began. The fresh
                // source bytes were read exactly once into the temporary file
                // and hashed; identity is now decided against the existing
                // artifact without re-reading either file. Replace semantics
                // intentionally bypass this decision.
                DeleteTemporary(ref temporaryPath);
                _originalTemporaryPath = null;
                if (string.Equals(existing.Sha256, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    _originalState = ExportArtifactState.VerifiedExisting;
                    return existing;
                }

                throw new SourceContentMismatchException(
                    "original",
                    existing.ByteLength,
                    artifact.ByteLength,
                    existing.Sha256,
                    artifact.Sha256);
            }

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
                if (_replace
                    && ExistingOriginalArtifact is not null
                    && !string.Equals(ExistingOriginalArtifact.Sha256, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    InvalidateDecodedArtifact();
                }
            }

            return artifact;
        }

        private static ExportArtifact ValidateComputedArtifact(string path, ExportArtifact computedArtifact, string relativePath)
        {
            var length = new FileInfo(path).Length;
            if (length != computedArtifact.ByteLength)
            {
                throw new SourceContentMismatchException(
                    "original",
                    computedArtifact.ByteLength,
                    length,
                    computedArtifact.Sha256,
                    "length-mismatch");
            }

            return new ExportArtifact(relativePath, computedArtifact.ByteLength, computedArtifact.Sha256);
        }

        private void InvalidateDecodedArtifact()
        {
            if (_existingDecodedArtifact is null && _decodedState == ExportArtifactState.Missing)
            {
                return;
            }

            if (File.Exists(_decodedPath))
            {
                File.Delete(_decodedPath);
            }

            _existingDecodedArtifact = null;
            _decodedState = ExportArtifactState.Missing;
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

    private sealed class FileSystemExportRunJournal : IExportRunLease
    {
        private readonly string _exportRoot;
        private readonly string _runId;
        private readonly FileStream _stream;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private bool _disposed;
        private int _disposeStarted;

        public FileSystemExportRunJournal(string exportRoot, string runId, FileStream stream)
        {
            _exportRoot = exportRoot;
            _runId = runId;
            _stream = stream;
        }

        public string RunId => _runId;

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

                await AppendCoreAsync(journalEvent, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task FinalizeAsync(VoiceExportManifest manifest, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(manifest);
            if (!string.Equals(manifest.RunId, _runId, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"The manifest RunId '{manifest.RunId}' does not match the active journal RunId '{_runId}'.");
            }

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(FileSystemExportRunJournal));
                }

                var journalPath = ExportPathSafety.CombineUnderRoot(_exportRoot, "runs", _runId + ".jsonl");
                var manifestPath = ExportPathSafety.CombineUnderRoot(_exportRoot, "runs", _runId + ".manifest.json");
                var latestPath = ExportPathSafety.CombineUnderRoot(_exportRoot, "latest.manifest.json");
                var journalManifest = await ReadManifestFromJournalAsync(manifest, journalPath, cancellationToken).ConfigureAwait(false);
                await AtomicFileWriter.WriteJsonAsync(manifestPath, journalManifest, InfrastructureJson.Indented, cancellationToken).ConfigureAwait(false);
                var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(journalManifest, InfrastructureJson.Indented);
                var manifestSha256 = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();
                await AppendCoreAsync(new VoiceExportJournalEvent("manifest-committed", _runId, DateTimeOffset.UtcNow, Context: null, ManifestSha256: manifestSha256), cancellationToken).ConfigureAwait(false);
                await AtomicFileWriter.WriteJsonAsync(latestPath, journalManifest, InfrastructureJson.Indented, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task AppendCoreAsync(VoiceExportJournalEvent journalEvent, CancellationToken cancellationToken)
        {
            if (!string.Equals(journalEvent.RunId, _runId, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"The journal event RunId '{journalEvent.RunId}' does not match the active journal RunId '{_runId}'.");
            }

            var line = JsonSerializer.Serialize(journalEvent, InfrastructureJson.Compact) + Environment.NewLine;
            var bytes = Encoding.UTF8.GetBytes(line);
            await _stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            {
                return;
            }

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                _disposed = true;
                await _stream.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
                _gate.Dispose();
            }
        }
    }
}
