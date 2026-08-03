using System.Text.Json;

namespace WeChatVoice.Infrastructure.Serialization;

/// <summary>
/// Writes files through a sibling temporary file so incomplete results are not
/// mistaken for successfully generated artifacts.
/// </summary>
internal static class AtomicFileWriter
{
    private const int BufferSize = 128 * 1024;

    internal static async Task WriteJsonAsync<T>(
        string destinationPath,
        T value,
        JsonSerializerOptions serializerOptions,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(serializerOptions);

        var directory = Path.GetDirectoryName(destinationPath)
            ?? throw new ArgumentException("The destination path must include a directory.", nameof(destinationPath));
        Directory.CreateDirectory(directory);

        var temporaryPath = CreateTemporarySibling(destinationPath);
        try
        {
            await using (var stream = OpenWrite(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream, value, serializerOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            CommitTemporary(temporaryPath, destinationPath);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    internal static async Task WriteTextAsync(
        string destinationPath,
        string content,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(content);
        var directory = Path.GetDirectoryName(destinationPath)
            ?? throw new ArgumentException("The destination path must include a directory.", nameof(destinationPath));
        Directory.CreateDirectory(directory);
        var temporaryPath = CreateTemporarySibling(destinationPath);
        try
        {
            await using (var stream = OpenWrite(temporaryPath))
            await using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false), BufferSize, leaveOpen: true))
            {
                await writer.WriteAsync(content.AsMemory(), cancellationToken).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            CommitTemporary(temporaryPath, destinationPath);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    internal static async Task WriteStreamAsync(
        string destinationPath,
        Func<Stream, CancellationToken, Task> write,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(write);
        var directory = Path.GetDirectoryName(destinationPath)
            ?? throw new ArgumentException("The destination path must include a directory.", nameof(destinationPath));
        Directory.CreateDirectory(directory);
        var temporaryPath = CreateTemporarySibling(destinationPath);
        try
        {
            await using (var stream = OpenWrite(temporaryPath))
            {
                await write(stream, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            CommitTemporary(temporaryPath, destinationPath);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    internal static string CreateTemporarySibling(string destinationPath)
    {
        var directory = Path.GetDirectoryName(destinationPath)
            ?? throw new ArgumentException("The destination path must include a directory.", nameof(destinationPath));
        var name = Path.GetFileName(destinationPath);
        return Path.Combine(directory, $".{name}.{Guid.NewGuid():N}.tmp");
    }

    private static FileStream OpenWrite(string path) => new(
        path,
        FileMode.CreateNew,
        FileAccess.Write,
        FileShare.None,
        BufferSize,
        FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static void CommitTemporary(string temporaryPath, string destinationPath)
    {
        if (!File.Exists(destinationPath))
        {
            File.Move(temporaryPath, destinationPath);
            return;
        }

        // File.Replace is the explicit same-volume atomic replacement path.
        // Supplying a unique sibling backup is more reliable on Windows than
        // the null-backup overload, which can fail while removing an existing
        // destination on some file systems. The backup is removed only after
        // the replacement succeeds.
        var backupPath = destinationPath + ".backup-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Replace(temporaryPath, destinationPath, backupPath, ignoreMetadataErrors: true);
        }
        finally
        {
            TryDelete(backupPath);
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
        catch (IOException)
        {
            // A failed cleanup must not hide the original write/cancellation error.
        }
        catch (UnauthorizedAccessException)
        {
            // A failed cleanup must not hide the original write/cancellation error.
        }
    }
}
