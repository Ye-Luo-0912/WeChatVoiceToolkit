using System.Buffers;
using System.Security.Cryptography;
using WeChatVoice.Core.Models;

namespace WeChatVoice.Infrastructure.Sqlite;

/// <summary>
/// Keeps every verified database group member open read-only with no write or
/// delete sharing. It also rechecks file identity, metadata, and selected
/// content through the held handles before a catalog operation uses the files.
/// </summary>
public sealed class VerifiedWorkspaceFileLease : IAsyncDisposable
{
    private readonly IReadOnlyList<WorkspaceFile> _files;
    private readonly IReadOnlyDictionary<string, IReadOnlySet<string>> _sidecarsByDirectory;
    private readonly SemaphoreSlim _verificationGate = new(1, 1);
    private readonly Dictionary<string, ContentVerificationStamp> _contentVerification = new(StringComparer.OrdinalIgnoreCase);
    private int _disposed;

    private VerifiedWorkspaceFileLease(
        IReadOnlyList<WorkspaceFile> files,
        IReadOnlyDictionary<string, IReadOnlySet<string>> sidecarsByDirectory)
    {
        _files = files;
        _sidecarsByDirectory = sidecarsByDirectory;
    }

    public static async Task<VerifiedWorkspaceFileLease> OpenAsync(
        VerifiedLocalWorkspace workspace,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        cancellationToken.ThrowIfCancellationRequested();

        var files = new List<WorkspaceFile>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = workspace.DataSet.Databases
            .Select(RequireLocalPath)
            .Select(Path.GetDirectoryName)
            .Where(static path => path is not null)
            .Select(static path => Path.GetFullPath(path!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var initialSidecars = CaptureSidecars(directories);
        if (initialSidecars.Values.Any(static sidecars => sidecars.Count > 0))
        {
            throw new WorkspaceVerificationException("The immutable local Workspace must not contain SQLite WAL or SHM sidecars.");
        }
        try
        {
            foreach (var artifact in workspace.DataSet.Databases)
            {
                var mainPath = RequireLocalPath(artifact);
                await AddAsync(files, paths, mainPath, artifact.LogicalRole, expectedPresent: true, artifact.MainSha256, artifact.MainLength, cancellationToken).ConfigureAwait(false);
                await AddAsync(files, paths, mainPath + "-wal", artifact.LogicalRole, artifact.WalPresent, artifact.WalSha256, artifact.WalLength, cancellationToken).ConfigureAwait(false);
                await AddAsync(files, paths, mainPath + "-shm", artifact.LogicalRole, artifact.ShmPresent, artifact.ShmSha256, artifact.ShmLength, cancellationToken).ConfigureAwait(false);
            }

            var lease = new VerifiedWorkspaceFileLease(files, initialSidecars);
            await lease.VerifyAsync(cancellationToken).ConfigureAwait(false);
            return lease;
        }
        catch
        {
            foreach (var file in files)
            {
                if (file.Stream is not null)
                {
                    await file.Stream.DisposeAsync().ConfigureAwait(false);
                }
            }

            throw;
        }
    }

    /// <summary>
    /// Rechecks all file identities and content. Content verification is cached
    /// for the lifetime of the held read-only handles; a metadata change
    /// invalidates only that file's cache and causes a fresh hash.
    /// </summary>
    public async Task VerifyAsync(CancellationToken cancellationToken, string? logicalRole = null)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        await _verificationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            VerifySidecars();
            foreach (var file in _files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (file.ExpectedPresent)
                {
                    var stamp = ReadCurrentStamp(file);
                    if (logicalRole is not null && !string.Equals(logicalRole, file.LogicalRole, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!_contentVerification.TryGetValue(file.Path, out var verified)
                        || !verified.MetadataEquals(stamp))
                    {
                        var actualHash = await HashThroughHandleAsync(file.Stream!, cancellationToken).ConfigureAwait(false);
                        if (!string.Equals(actualHash, file.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new WorkspaceVerificationException($"A verified workspace database file content changed: '{file.Path}'.");
                        }

                        _contentVerification[file.Path] = stamp;
                    }
                }
                else if (File.Exists(file.Path))
                {
                    throw new WorkspaceVerificationException($"An unexpected SQLite sidecar appeared: '{file.Path}'.");
                }
            }
        }
        finally
        {
            _verificationGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var file in _files)
        {
            if (file.Stream is not null)
            {
                await file.Stream.DisposeAsync().ConfigureAwait(false);
            }
        }

        _verificationGate.Dispose();
    }

    private static ContentVerificationStamp ReadCurrentStamp(WorkspaceFile file)
    {
        if (file.Stream is null || !File.Exists(file.Path))
        {
            throw new WorkspaceVerificationException($"A verified workspace database file disappeared: '{file.Path}'.");
        }

        var actualIdentity = FileIdentity.Read(file.Stream);
        var actualLength = file.Stream.Length;
        var actualWriteTicks = File.GetLastWriteTimeUtc(file.Path).Ticks;
        if (!string.Equals(actualIdentity, file.FileId, StringComparison.Ordinal)
            || actualLength != file.ExpectedLength
            || actualWriteTicks != file.LastWriteUtcTicks)
        {
            throw new WorkspaceVerificationException($"A verified workspace database file changed: '{file.Path}'.");
        }

        return new ContentVerificationStamp(actualIdentity, actualLength, actualWriteTicks);
    }

    private void VerifySidecars()
    {
        var current = CaptureSidecars(_sidecarsByDirectory.Keys);
        foreach (var directory in _sidecarsByDirectory.Keys)
        {
            var expected = _sidecarsByDirectory[directory];
            var actual = current.TryGetValue(directory, out var found)
                ? found
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!expected.SetEquals(actual))
            {
                throw new WorkspaceVerificationException("SQLite sidecar files changed while the verified Workspace was open.");
            }
        }
    }

    private static IReadOnlyDictionary<string, IReadOnlySet<string>> CaptureSidecars(IEnumerable<string> directories)
    {
        var result = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in directories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var sidecars = Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                    .Where(static path => path.EndsWith("-wal", StringComparison.OrdinalIgnoreCase)
                        || path.EndsWith("-shm", StringComparison.OrdinalIgnoreCase))
                    .Select(Path.GetFullPath)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            result[Path.GetFullPath(directory)] = sidecars;
        }

        return result;
    }

    private static async Task AddAsync(
        ICollection<WorkspaceFile> files,
        ISet<string> paths,
        string path,
        string logicalRole,
        bool expectedPresent,
        string? expectedSha256,
        long? expectedLength,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        if (!paths.Add(fullPath))
        {
            throw new WorkspaceVerificationException($"A workspace database group references the same file more than once: '{fullPath}'.");
        }

        if (!expectedPresent)
        {
            files.Add(new WorkspaceFile(fullPath, logicalRole, false, null, string.Empty, 0, string.Empty, 0));
            return;
        }

        if (string.IsNullOrWhiteSpace(expectedSha256) || expectedLength is null or < 0)
        {
            throw new WorkspaceVerificationException($"Verified workspace metadata is incomplete for '{fullPath}'.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new WorkspaceVerificationException($"A verified workspace database file is a reparse point: '{fullPath}'.");
        }

        var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        files.Add(new WorkspaceFile(
            fullPath,
            logicalRole,
            true,
            stream,
            expectedSha256,
            expectedLength.Value,
            FileIdentity.Read(stream),
            File.GetLastWriteTimeUtc(fullPath).Ticks));
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static string RequireLocalPath(DatabaseArtifact artifact)
        => string.IsNullOrWhiteSpace(artifact.LocalPath)
            ? throw new WorkspaceVerificationException($"Verified workspace artifact '{artifact.DatabasePath}' lacks a local path.")
            : artifact.LocalPath;

    private static async Task<string> HashThroughHandleAsync(FileStream stream, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            stream.Position = 0;
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
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

    private sealed record WorkspaceFile(
        string Path,
        string LogicalRole,
        bool ExpectedPresent,
        FileStream? Stream,
        string ExpectedSha256,
        long ExpectedLength,
        string FileId,
        long LastWriteUtcTicks);

    private sealed record ContentVerificationStamp(
        string FileId,
        long Length,
        long LastWriteUtcTicks)
    {
        public bool MetadataEquals(ContentVerificationStamp other)
            => string.Equals(FileId, other.FileId, StringComparison.Ordinal)
                && Length == other.Length
                && LastWriteUtcTicks == other.LastWriteUtcTicks;
    }
}
