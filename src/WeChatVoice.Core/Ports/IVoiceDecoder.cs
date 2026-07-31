namespace WeChatVoice.Core.Ports;

/// <summary>
/// Converts a SILK payload stream to a WAV output stream without exposing
/// physical export paths to the application layer.
/// </summary>
public interface IVoiceDecoder
{
    async Task DecodeAsync(Stream input, Stream output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        cancellationToken.ThrowIfCancellationRequested();

        var directory = Path.Combine(Path.GetTempPath(), "wechatvoice-decoder");
        Directory.CreateDirectory(directory);
        var token = Guid.NewGuid().ToString("N");
        var inputPath = Path.Combine(directory, $"{token}.input.silk");
        var outputPath = Path.Combine(directory, $"{token}.output.wav");
        try
        {
            await using (var inputFile = new FileStream(inputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await input.CopyToAsync(inputFile, 128 * 1024, cancellationToken).ConfigureAwait(false);
                await inputFile.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            await DecodeAsync(inputPath, outputPath, cancellationToken).ConfigureAwait(false);
            await using var outputFile = new FileStream(outputPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await outputFile.CopyToAsync(output, 128 * 1024, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(inputPath);
            TryDelete(outputPath);
        }
    }

    [Obsolete("Use the stream-based overload.")]
    Task DecodeAsync(string inputPath, string outputPath, CancellationToken cancellationToken)
        => throw new NotSupportedException("This decoder does not implement path decoding.");

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
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
