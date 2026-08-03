using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace WeChatVoice.Infrastructure.Audio;

/// <summary>
/// Shared persistence primitive for local JSONL optimization caches. Writers
/// are serialized across processes, append lines while the index is small,
/// and periodically rebuild the file from the current in-memory winners.
/// </summary>
internal sealed class JsonlCacheFileStore
{
    internal const long CompactThresholdBytes = 8L * 1024 * 1024;
    internal const int CompactMinimumLines = 256;
    internal const int MaximumEntries = 100_000;
    internal static readonly TimeSpan EntryRetention = TimeSpan.FromDays(180);

    private readonly string _path;

    internal JsonlCacheFileStore(string path)
        => _path = Path.GetFullPath(path);

    internal async Task<JsonlCacheWriteLock> AcquireWriteLockAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidDataException("The JSONL cache path has no parent directory.");
        Directory.CreateDirectory(directory);
        var lockPath = _path + ".lock";
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 5;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileStream? stream = null;
            try
            {
                stream = new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.Asynchronous);
                return new JsonlCacheWriteLock(stream);
            }
            catch (IOException exception)
            {
                if (stream is not null)
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                }

                if (Stopwatch.GetTimestamp() >= deadline)
                {
                    throw new IOException("The JSONL cache is busy in another process.", exception);
                }

                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal async Task AppendLineAsync(string line, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(line);
        await using var stream = new FileStream(
            _path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read | FileShare.Delete,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var bytes = Encoding.UTF8.GetBytes(line + Environment.NewLine);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task ReplaceAsync(
        IEnumerable<string> lines,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidDataException("The JSONL cache path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = _path + ".compact-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                foreach (var line in lines)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var bytes = Encoding.UTF8.GetBytes(line + Environment.NewLine);
                    await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                }

                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
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

    internal static bool ShouldCompact(
        long length,
        int lineCount,
        int uniqueCount,
        bool requiresMigration = false)
        => length >= CompactThresholdBytes
            || lineCount >= CompactMinimumLines
                && lineCount > checked(uniqueCount * 2 + 32)
            || uniqueCount > MaximumEntries
            || requiresMigration;

    internal static string HashSourceStableKey(string sourceStableKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceStableKey);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceStableKey))).ToLowerInvariant();
    }
}

internal sealed class JsonlCacheWriteLock : IAsyncDisposable
{
    private readonly FileStream _stream;
    private int _disposed;

    internal JsonlCacheWriteLock(FileStream stream)
        => _stream = stream;

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            return _stream.DisposeAsync();
        }

        return ValueTask.CompletedTask;
    }
}
