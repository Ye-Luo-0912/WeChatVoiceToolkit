using System.Buffers;
using System.Security.Cryptography;
using WeChatVoice.Core.Errors;
using WeChatVoice.Core.Models;

namespace WeChatVoice.KeyBroker;

/// <summary>
/// Holds the DB/WAL/SHM group open for read-only with no write or delete
/// share, and re-derives the group fingerprint through the open handles so
/// the worker can only ever see exactly the verified bytes. The fingerprint
/// uses the same canonical form as dataset probing and workspace
/// verification via <see cref="DatabaseGroupFingerprint"/>.
/// </summary>
internal sealed class VerifiedDatabaseGroupLease : IAsyncDisposable
{
    private readonly FileStream _main;
    private readonly FileStream? _wal;
    private readonly FileStream? _shm;

    private VerifiedDatabaseGroupLease(FileStream main, FileStream? wal, FileStream? shm)
    {
        _main = main;
        _wal = wal;
        _shm = shm;
    }

    internal string MainPath => _main.Name;

    internal static async Task<VerifiedDatabaseGroupLease> OpenAsync(
        string mainPath,
        DatabaseGroupTarget target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();

        var streams = new List<FileStream>(3);
        FileStream? mainStream = null;
        FileStream? walStream = null;
        FileStream? shmStream = null;
        try
        {
            foreach (var path in new[] { mainPath, mainPath + "-wal", mainPath + "-shm" })
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!File.Exists(path))
                {
                    continue;
                }

                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new AppFailureException(ErrorCode.SnapshotInconsistent, $"A database group member is a reparse point: '{path}'.");
                }

                var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                streams.Add(stream);
                if (string.Equals(path, mainPath, StringComparison.OrdinalIgnoreCase))
                {
                    mainStream = stream;
                }
                else if (path.EndsWith("-wal", StringComparison.OrdinalIgnoreCase))
                {
                    walStream = stream;
                }
                else
                {
                    shmStream = stream;
                }
            }

            if (mainStream is null)
            {
                throw new AppFailureException(ErrorCode.SnapshotInvalid, $"The database group main file is missing: '{mainPath}'.");
            }

            var lease = new VerifiedDatabaseGroupLease(mainStream, walStream, shmStream);
            try
            {
                var mainHash = await HashThroughHandleAsync(mainStream, cancellationToken).ConfigureAwait(false);
                var walHash = walStream is null ? null : await HashThroughHandleAsync(walStream, cancellationToken).ConfigureAwait(false);
                var shmHash = shmStream is null ? null : await HashThroughHandleAsync(shmStream, cancellationToken).ConfigureAwait(false);
                var actual = DatabaseGroupFingerprint.Compute(
                    target.SourceRelativePath,
                    target.LogicalRole,
                    target.ShardNumber,
                    mainStream.Length,
                    mainHash,
                    walStream?.Length,
                    walHash,
                    shmStream?.Length,
                    shmHash);
                if (!string.Equals(actual, target.DatabaseGroupFingerprint, StringComparison.OrdinalIgnoreCase))
                {
                    throw new AppFailureException(ErrorCode.SnapshotInconsistent, $"The database group '{target.SourceRelativePath}' changed after the verified Snapshot was staged.");
                }

                return lease;
            }
            catch
            {
                await lease.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        catch
        {
            foreach (var stream in streams)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        var main = _main;
        var wal = _wal;
        var shm = _shm;
        return new ValueTask(DisposeAllAsync(main, wal, shm));
    }

    private static async Task DisposeAllAsync(FileStream main, FileStream? wal, FileStream? shm)
    {
        await main.DisposeAsync().ConfigureAwait(false);
        if (wal is not null)
        {
            await wal.DisposeAsync().ConfigureAwait(false);
        }

        if (shm is not null)
        {
            await shm.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<string> HashThroughHandleAsync(Stream stream, CancellationToken cancellationToken)
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
}
