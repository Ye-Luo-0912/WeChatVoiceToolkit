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
        ExportExistingPolicy policy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();

        var occurredAtUtc = record.OccurredAtUtc.ToUniversalTime();
        var year = occurredAtUtc.ToString("yyyy", CultureInfo.InvariantCulture);
        var month = occurredAtUtc.ToString("MM", CultureInfo.InvariantCulture);
        var sourceId = ExportPathSafety.SanitizeFileStem(record.MessageId, "voice");
        var stableSuffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(record.MessageId)))
            .ToLowerInvariant()[..12];
        var baseName = $"{sourceId[..Math.Min(sourceId.Length, 80)]}-{stableSuffix}";

        for (var attempt = 0; attempt < int.MaxValue; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileName = attempt == 0 ? baseName : $"{baseName}-{attempt:D4}";
            var originalManifestPath = $"original/{year}/{month}/{fileName}.silk";
            var decodedManifestPath = $"decoded/{year}/{month}/{fileName}.wav";
            var originalPath = ExportPathSafety.CombineUnderRoot(_exportRoot, "original", year, month, $"{fileName}.silk");
            var decodedPath = ExportPathSafety.CombineUnderRoot(_exportRoot, "decoded", year, month, $"{fileName}.wav");

            var alreadyExists = File.Exists(originalPath) || File.Exists(decodedPath);
            if (alreadyExists && policy == ExportExistingPolicy.Skip)
            {
                return ValueTask.FromResult<IExportItemLease>(new FileSystemExportItemLease(
                    record,
                    originalManifestPath,
                    decodedManifestPath,
                    originalPath,
                    decodedPath,
                    isSkipped: true,
                    release: null));
            }

            if (alreadyExists)
            {
                continue;
            }

            if (!_reservedPaths.TryAdd(originalPath, 0))
            {
                continue;
            }

            if (!_reservedPaths.TryAdd(decodedPath, 0))
            {
                _reservedPaths.TryRemove(originalPath, out _);
                continue;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(originalPath)!);
                Directory.CreateDirectory(Path.GetDirectoryName(decodedPath)!);
                return ValueTask.FromResult<IExportItemLease>(new FileSystemExportItemLease(
                    record,
                    originalManifestPath,
                    decodedManifestPath,
                    originalPath,
                    decodedPath,
                    isSkipped: false,
                    release: () => Release(originalPath, decodedPath)));
            }
            catch
            {
                Release(originalPath, decodedPath);
                throw;
            }
        }

        throw new IOException("A unique export file name could not be allocated.");
    }

    public Task FinalizeRunAsync(VoiceExportManifest manifest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return AtomicFileWriter.WriteJsonAsync(
            ExportPathSafety.CombineUnderRoot(_exportRoot, "manifest.json"),
            manifest,
            InfrastructureJson.Indented,
            cancellationToken);
    }

    // Compatibility shims for callers of the pre-lease foundation. The
    // application no longer uses these methods.
    [Obsolete("Use BeginItemAsync.")]
    public ValueTask<VoiceExportPaths> CreatePathsAsync(VoiceMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var locator = new VoicePayloadLocator("legacy", null, message.PayloadReference ?? message.MessageId);
        var record = new VoiceRecord(message.MessageId, message.ConversationId, message.OccurredAtUtc, message.Direction, locator);
        var occurredAtUtc = record.OccurredAtUtc.ToUniversalTime();
        var year = occurredAtUtc.ToString("yyyy", CultureInfo.InvariantCulture);
        var month = occurredAtUtc.ToString("MM", CultureInfo.InvariantCulture);
        var sourceId = ExportPathSafety.SanitizeFileStem(record.MessageId, "voice");
        var stableSuffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(record.MessageId))).ToLowerInvariant()[..12];
        var baseName = $"{sourceId[..Math.Min(sourceId.Length, 80)]}-{stableSuffix}";

        for (var attempt = 0; attempt < int.MaxValue; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileName = attempt == 0 ? baseName : $"{baseName}-{attempt:D4}";
            var originalManifestPath = $"original/{year}/{month}/{fileName}.silk";
            var decodedManifestPath = $"decoded/{year}/{month}/{fileName}.wav";
            var originalPath = ExportPathSafety.CombineUnderRoot(_exportRoot, "original", year, month, $"{fileName}.silk");
            var decodedPath = ExportPathSafety.CombineUnderRoot(_exportRoot, "decoded", year, month, $"{fileName}.wav");
            if (File.Exists(originalPath) || File.Exists(decodedPath))
            {
                continue;
            }

            if (!_reservedPaths.TryAdd(originalPath, 0))
            {
                continue;
            }

            if (!_reservedPaths.TryAdd(decodedPath, 0))
            {
                _reservedPaths.TryRemove(originalPath, out _);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(originalPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(decodedPath)!);
            return ValueTask.FromResult(new VoiceExportPaths(originalPath, decodedPath, originalManifestPath, decodedManifestPath));
        }

        throw new IOException("A unique export file name could not be allocated.");
    }

    [Obsolete("Use lease streams.")]
    public Task WriteOriginalAsync(VoiceExportPaths paths, Stream source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        EnsureOwnedPath(paths.OriginalFilePath, ".silk");
        return AtomicFileWriter.CopyStreamAsync(source, paths.OriginalFilePath, overwrite: false, cancellationToken: cancellationToken);
    }

    [Obsolete("Use lease streams.")]
    public Task WriteDecodedAsync(VoiceExportPaths paths, Stream source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        EnsureOwnedPath(paths.DecodedFilePath, ".wav");
        return AtomicFileWriter.CopyStreamAsync(source, paths.DecodedFilePath, overwrite: false, cancellationToken: cancellationToken);
    }

    [Obsolete("Use FinalizeRunAsync.")]
    public Task WriteManifestAsync(VoiceExportManifest manifest, CancellationToken cancellationToken)
        => FinalizeRunAsync(manifest, cancellationToken);

    private void Release(string originalPath, string decodedPath)
    {
        _reservedPaths.TryRemove(originalPath, out _);
        _reservedPaths.TryRemove(decodedPath, out _);
    }

    private void EnsureOwnedPath(string path, string expectedExtension)
    {
        if (!string.Equals(Path.GetExtension(path), expectedExtension, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"The path must use the {expectedExtension} extension.", nameof(path));
        }

        var fullPath = Path.GetFullPath(path);
        var rootWithSeparator = _exportRoot.EndsWith(Path.DirectorySeparatorChar)
            ? _exportRoot
            : _exportRoot + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The export path is outside this store's export root.");
        }
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
            Action? release)
        {
            Record = record;
            OriginalManifestPath = originalManifestPath;
            DecodedManifestPath = decodedManifestPath;
            _originalPath = originalPath;
            _decodedPath = decodedPath;
            IsSkipped = isSkipped;
            _release = release;
        }

        public VoiceRecord Record { get; }

        public bool IsSkipped { get; }

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
