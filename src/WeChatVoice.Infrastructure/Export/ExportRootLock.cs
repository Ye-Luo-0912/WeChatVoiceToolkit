using System.Diagnostics;
using System.Text.Json;

namespace WeChatVoice.Infrastructure.Export;

public enum ExportRootLockMode
{
    Shared,
    Exclusive,
}

public sealed class ExportRootBusyException : IOException
{
    public ExportRootBusyException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Cross-process lock for operations that inspect or publish an export root.
/// The lock file is retained as a small diagnostic record; the open handle is
/// the actual synchronization primitive.
/// </summary>
public sealed class ExportRootLock : IAsyncDisposable
{
    private const string MetadataFormat = "wechatvoice-export-lock-v1";
    private readonly FileStream _stream;
    private int _disposed;

    private ExportRootLock(FileStream stream, string path, ExportRootLockMode mode)
    {
        _stream = stream;
        Path = path;
        Mode = mode;
    }

    public string Path { get; }
    public ExportRootLockMode Mode { get; }

    public static async ValueTask<ExportRootLock> AcquireAsync(
        string exportRoot,
        ExportRootLockMode mode,
        string operationId,
        string? runId,
        CancellationToken cancellationToken,
        bool waitForAvailability = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exportRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        cancellationToken.ThrowIfCancellationRequested();

        var root = System.IO.Path.GetFullPath(exportRoot);
        var metadataDirectory = System.IO.Path.Combine(root, ".wechatvoice");
        Directory.CreateDirectory(metadataDirectory);
        var path = System.IO.Path.Combine(metadataDirectory, "export.lock");
        if (mode == ExportRootLockMode.Shared && !File.Exists(path))
        {
            try
            {
                using var create = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite, 1, FileOptions.SequentialScan);
            }
            catch (IOException) when (File.Exists(path))
            {
                // Another reader or writer created the retained lock record.
            }
        }
        FileStream? stream = null;
        var retryUntil = waitForAvailability ? DateTime.UtcNow + TimeSpan.FromSeconds(30) : DateTime.MinValue;
        while (stream is null)
        {
            try
            {
                stream = new FileStream(
                    path,
                    FileMode.OpenOrCreate,
                    mode == ExportRootLockMode.Exclusive ? FileAccess.ReadWrite : FileAccess.Read,
                    // Shared readers may share read access with one another,
                    // but they must deny write access so an exclusive
                    // publisher cannot enter while a verifier is reading.
                    mode == ExportRootLockMode.Exclusive ? FileShare.None : FileShare.Read,
                    4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
            }
            catch (IOException) when (waitForAvailability && DateTime.UtcNow < retryUntil)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(false);
            }
            catch (IOException exception)
            {
                throw new ExportRootBusyException("The export root is busy with another operation.", exception);
            }
            catch (UnauthorizedAccessException exception)
            {
                throw new ExportRootBusyException("The export root lock cannot be acquired.", exception);
            }
        }

        var lease = new ExportRootLock(stream, path, mode);
        if (mode == ExportRootLockMode.Exclusive)
        {
            try
            {
                var processStart = TryGetProcessStartTimeUtc();
                var metadata = JsonSerializer.SerializeToUtf8Bytes(new LockMetadata(
                    MetadataFormat,
                    operationId,
                    runId,
                    Environment.ProcessId,
                    processStart,
                    DateTimeOffset.UtcNow));
                stream.SetLength(0);
                stream.Position = 0;
                await stream.WriteAsync(metadata, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            catch
            {
                await lease.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        return lease;
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _stream.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private static DateTimeOffset? TryGetProcessStartTimeUtc()
    {
        try
        {
            return Process.GetCurrentProcess().StartTime.ToUniversalTime();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private sealed record LockMetadata(
        string Format,
        string OperationId,
        string? RunId,
        int ProcessId,
        DateTimeOffset? ProcessStartTimeUtc,
        DateTimeOffset AcquiredAtUtc);
}
