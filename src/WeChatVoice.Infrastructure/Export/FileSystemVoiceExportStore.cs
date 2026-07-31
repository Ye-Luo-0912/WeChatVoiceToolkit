using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using WeChatVoice.Core.Models;
using WeChatVoice.Core.Ports;
using WeChatVoice.Infrastructure.Serialization;

namespace WeChatVoice.Infrastructure.Export;

/// <summary>
/// A local export store that keeps original SILK and derived WAV artifacts in
/// separate date-based directory trees. All physical path handling is kept in
/// item leases owned by this store.
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

    public ValueTask<IExportItemLease> BeginItemAsync(
        VoiceRecord record,
        ExistingArtifactPolicy policy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();

        var occurredAtUtc = record.OccurredAtUtc.ToUniversalTime();
        var year = occurredAtUtc.ToString("yyyy", CultureInfo.InvariantCulture);
        var month = occurredAtUtc.ToString("MM", CultureInfo.InvariantCulture);
        var sourceId = ExportPathSafety.SanitizeFileStem(record.SourceMessageKey, "voice");
        var stableSuffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(record.StableExportKey)))
            .ToLowerInvariant()[..16];
        var fileName = $"{sourceId[..Math.Min(sourceId.Length, 64)]}-{stableSuffix}";
        var originalManifestPath = $"original/{year}/{month}/{fileName}.silk";
        var decodedManifestPath = $"decoded/{year}/{month}/{fileName}.wav";
        var originalPath = ExportPathSafety.CombineUnderRoot(_exportRoot, "original", year, month, $"{fileName}.silk");
        var decodedPath = ExportPathSafety.CombineUnderRoot(_exportRoot, "decoded", year, month, $"{fileName}.wav");

        var originalExists = File.Exists(originalPath);
        var decodedExists = File.Exists(decodedPath);
        if (originalExists)
        {
            var existing = new ExportArtifact(originalManifestPath, new FileInfo(originalPath).Length, ComputeSha256(originalPath));
            if (policy is ExistingArtifactPolicy.SkipIfHashMatches or ExistingArtifactPolicy.VerifyOnly)
            {
                if (string.IsNullOrWhiteSpace(record.PayloadSha256))
                {
                    throw new ExistingArtifactNeedsHashException($"Existing artifact '{originalManifestPath}' cannot be safely reused because the source payload hash is unknown.");
                }

                if (!string.Equals(existing.Sha256, record.PayloadSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ExistingArtifactConflictException($"Existing artifact '{originalManifestPath}' has a different SHA-256 than the source payload.");
                }

                return ValueTask.FromResult<IExportItemLease>(new FileSystemExportItemLease(
                    record, originalManifestPath, decodedManifestPath, originalPath, decodedPath, true, existing, null));
            }

            if (policy == ExistingArtifactPolicy.Fail)
            {
                throw new ExistingArtifactConflictException($"An export artifact already exists for stable key '{record.StableExportKey}'.");
            }

            if (policy == ExistingArtifactPolicy.Replace)
            {
                File.Delete(originalPath);
                if (decodedExists)
                {
                    File.Delete(decodedPath);
                }
            }
        }
        else if (decodedExists)
        {
            if (policy != ExistingArtifactPolicy.Replace)
            {
                throw new ExistingArtifactConflictException($"A derived artifact already exists without its original artifact for stable key '{record.StableExportKey}'.");
            }

            File.Delete(decodedPath);
        }

        if (!_reservedPaths.TryAdd(originalPath, 0))
        {
            throw new ExistingArtifactConflictException($"An export for stable key '{record.StableExportKey}' is already in progress.");
        }

        if (!_reservedPaths.TryAdd(decodedPath, 0))
        {
            _reservedPaths.TryRemove(originalPath, out _);
            throw new ExistingArtifactConflictException($"An export for stable key '{record.StableExportKey}' is already in progress.");
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(originalPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(decodedPath)!);
            return ValueTask.FromResult<IExportItemLease>(new FileSystemExportItemLease(
                record, originalManifestPath, decodedManifestPath, originalPath, decodedPath, false, null,
                () => Release(originalPath, decodedPath)));
        }
        catch
        {
            Release(originalPath, decodedPath);
            throw;
        }
    }

    public Task FinalizeRunAsync(VoiceExportManifest manifest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var runsDirectory = ExportPathSafety.CombineUnderRoot(_exportRoot, "runs");
        Directory.CreateDirectory(runsDirectory);
        var manifestPath = Path.Combine(runsDirectory, manifest.RunId + ".manifest.json");
        var journalPath = Path.Combine(runsDirectory, manifest.RunId + ".jsonl");
        var latestPath = ExportPathSafety.CombineUnderRoot(_exportRoot, "latest.manifest.json");
        return FinalizeFilesAsync(manifest, manifestPath, journalPath, latestPath, cancellationToken);
    }

    private static async Task FinalizeFilesAsync(
        VoiceExportManifest manifest,
        string manifestPath,
        string journalPath,
        string latestPath,
        CancellationToken cancellationToken)
    {
        await AtomicFileWriter.WriteJsonAsync(manifestPath, manifest, InfrastructureJson.Indented, cancellationToken).ConfigureAwait(false);
        var journal = string.Join(Environment.NewLine, manifest.Entries.Select(entry => System.Text.Json.JsonSerializer.Serialize(entry, InfrastructureJson.Compact))) + Environment.NewLine;
        await AtomicFileWriter.WriteTextAsync(journalPath, journal, cancellationToken).ConfigureAwait(false);
        await AtomicFileWriter.WriteJsonAsync(latestPath, manifest, InfrastructureJson.Indented, cancellationToken).ConfigureAwait(false);
    }

    private static string ComputeSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan);
        using var hasher = SHA256.Create();
        return Convert.ToHexString(hasher.ComputeHash(stream)).ToLowerInvariant();
    }

    private void Release(string originalPath, string decodedPath)
    {
        _reservedPaths.TryRemove(originalPath, out _);
        _reservedPaths.TryRemove(decodedPath, out _);
    }

    private sealed class FileSystemExportItemLease : IExportItemLease
    {
        private readonly string _originalPath;
        private readonly string _decodedPath;
        private readonly Action? _release;
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
            bool isSkipped,
            ExportArtifact? existingOriginalArtifact,
            Action? release)
        {
            Record = record;
            OriginalManifestPath = originalManifestPath;
            DecodedManifestPath = decodedManifestPath;
            _originalPath = originalPath;
            _decodedPath = decodedPath;
            IsSkipped = isSkipped;
            ExistingOriginalArtifact = existingOriginalArtifact;
            _release = release;
        }

        public VoiceRecord Record { get; }

        public bool IsSkipped { get; }

        public ExportArtifact? ExistingOriginalArtifact { get; }

        public string OriginalManifestPath { get; }

        public string DecodedManifestPath { get; }

        public ValueTask<Stream> OpenOriginalWriteAsync(CancellationToken cancellationToken)
            => OpenWriteAsync(isDecoded: false, cancellationToken);

        public ValueTask<Stream> OpenOriginalReadAsync(CancellationToken cancellationToken)
        {
            EnsureUsable();
            if (IsSkipped || !_originalCommitted)
            {
                throw new InvalidOperationException("The original artifact must be committed before it can be read.");
            }

            return ValueTask.FromResult<Stream>(new FileStream(
                _originalPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan));
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
                _release?.Invoke();
            }
        }

        private ValueTask<Stream> OpenWriteAsync(bool isDecoded, CancellationToken cancellationToken)
        {
            EnsureUsable();
            if (IsSkipped)
            {
                throw new InvalidOperationException("This item was skipped because an artifact already exists.");
            }

            if (isDecoded ? _decodedCommitted : _originalCommitted)
            {
                throw new InvalidOperationException("The artifact has already been committed.");
            }

            if (isDecoded ? _decodedTemporaryPath is not null : _originalTemporaryPath is not null)
            {
                throw new InvalidOperationException("A write stream for this artifact is already open.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var finalPath = isDecoded ? _decodedPath : _originalPath;
            var temporaryPath = Path.Combine(
                Path.GetDirectoryName(finalPath)!,
                $".{Path.GetFileName(finalPath)}.{Guid.NewGuid():N}.tmp");
            if (isDecoded)
            {
                _decodedTemporaryPath = temporaryPath;
            }
            else
            {
                _originalTemporaryPath = temporaryPath;
            }

            Stream stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return ValueTask.FromResult(stream);
        }

        private async Task<ExportArtifact> CommitAsync(bool isDecoded, CancellationToken cancellationToken)
        {
            EnsureUsable();
            if (IsSkipped)
            {
                throw new InvalidOperationException("This item was skipped because an artifact already exists.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var temporaryPath = isDecoded ? _decodedTemporaryPath : _originalTemporaryPath;
            if (temporaryPath is null)
            {
                throw new InvalidOperationException("No temporary artifact has been opened.");
            }

            var finalPath = isDecoded ? _decodedPath : _originalPath;
            var artifact = await ComputeArtifactAsync(temporaryPath, isDecoded ? DecodedManifestPath : OriginalManifestPath, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, finalPath);
            if (isDecoded)
            {
                _decodedTemporaryPath = null;
            }
            else
            {
                _originalTemporaryPath = null;
            }
            if (!isDecoded)
            {
                _originalCommitted = true;
            }
            else
            {
                _decodedCommitted = true;
            }

            return artifact;
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

            return new ExportArtifact(relativePath, length, Convert.ToHexString(hash.GetHashAndReset()));
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
}
