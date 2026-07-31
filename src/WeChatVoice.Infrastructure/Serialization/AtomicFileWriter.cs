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
            }

            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    internal static async Task CopyStreamAsync(
        Stream source,
        string destinationPath,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        var directory = Path.GetDirectoryName(destinationPath)
            ?? throw new ArgumentException("The destination path must include a directory.", nameof(destinationPath));
        Directory.CreateDirectory(directory);

        var temporaryPath = CreateTemporarySibling(destinationPath);
        try
        {
            await using (var destination = OpenWrite(temporaryPath))
            {
                await source.CopyToAsync(destination, BufferSize, cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, destinationPath, overwrite);
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
            await File.WriteAllTextAsync(temporaryPath, content, System.Text.Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, destinationPath, overwrite: true);
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
