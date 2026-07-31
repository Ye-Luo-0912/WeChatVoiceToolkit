using System.ComponentModel;
using System.Diagnostics;
using WeChatVoice.Core.Ports;

namespace WeChatVoice.Infrastructure.Audio;

/// <summary>
/// Delegates SILK-to-WAV conversion to an explicitly configured local decoder.
/// The input is copied to a temporary file so the external process never receives
/// the caller's original SILK path, and its output is moved into place only after
/// a successful exit.
/// </summary>
public sealed class ExternalSilkDecoder : IVoiceDecoder
{
    private const int BufferSize = 128 * 1024;
    private readonly string _executablePath;
    private readonly int _sampleRate;
    private readonly string? _workingDirectory;

    public ExternalSilkDecoder(string executablePath, int sampleRate = 24000, string? workingDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

        _executablePath = Path.GetFullPath(executablePath);
        _sampleRate = sampleRate;
        _workingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
            ? null
            : Path.GetFullPath(workingDirectory);
    }

    public async Task DecodeAsync(string inputPath, string outputPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        cancellationToken.ThrowIfCancellationRequested();

        var fullInputPath = Path.GetFullPath(inputPath);
        var fullOutputPath = Path.GetFullPath(outputPath);
        if (!File.Exists(fullInputPath))
        {
            throw new FileNotFoundException("The SILK input file was not found.", fullInputPath);
        }

        if (!File.Exists(_executablePath))
        {
            throw new FileNotFoundException("The configured SILK decoder executable was not found.", _executablePath);
        }

        if (!string.Equals(Path.GetExtension(fullOutputPath), ".wav", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The decoder output path must use the .wav extension.", nameof(outputPath));
        }

        if (string.Equals(fullInputPath, fullOutputPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The decoded output must not overwrite the original input.", nameof(outputPath));
        }

        if (File.Exists(fullOutputPath))
        {
            throw new IOException($"The decoded output already exists: '{fullOutputPath}'.");
        }

        var outputDirectory = Path.GetDirectoryName(fullOutputPath)
            ?? throw new ArgumentException("The decoder output path must include a directory.", nameof(outputPath));
        Directory.CreateDirectory(outputDirectory);

        var token = Guid.NewGuid().ToString("N");
        var temporaryInputPath = Path.Combine(outputDirectory, $".{Path.GetFileName(fullOutputPath)}.{token}.input.silk");
        var temporaryOutputPath = Path.Combine(outputDirectory, $".{Path.GetFileName(fullOutputPath)}.{token}.output.wav");

        try
        {
            await CopyInputAsync(fullInputPath, temporaryInputPath, cancellationToken).ConfigureAwait(false);
            var result = await RunDecoderAsync(temporaryInputPath, temporaryOutputPath, cancellationToken).ConfigureAwait(false);

            if (result.ExitCode != 0)
            {
                throw new ExternalSilkDecoderException(
                    result.ExitCode,
                    result.StandardOutput,
                    result.StandardError,
                    "The external SILK decoder returned a non-zero exit code.");
            }

            if (!File.Exists(temporaryOutputPath) || new FileInfo(temporaryOutputPath).Length == 0)
            {
                throw new ExternalSilkDecoderException(
                    result.ExitCode,
                    result.StandardOutput,
                    result.StandardError,
                    "The external SILK decoder completed without producing a WAV file.");
            }

            // The staging file is a sibling of the requested output, so this
            // no-overwrite move is atomic on the normal local filesystem path.
            File.Move(temporaryOutputPath, fullOutputPath);
        }
        finally
        {
            TryDelete(temporaryInputPath);
            TryDelete(temporaryOutputPath);
        }
    }

    private async Task<DecoderProcessResult> RunDecoderAsync(
        string temporaryInputPath,
        string temporaryOutputPath,
        CancellationToken cancellationToken)
    {
        var workingDirectory = _workingDirectory ?? Path.GetDirectoryName(_executablePath);
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            throw new DirectoryNotFoundException("The configured SILK decoder working directory was not found.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _executablePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--input");
        startInfo.ArgumentList.Add(temporaryInputPath);
        startInfo.ArgumentList.Add("--output");
        startInfo.ArgumentList.Add(temporaryOutputPath);
        startInfo.ArgumentList.Add("--sample-rate");
        startInfo.ArgumentList.Add(_sampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture));

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new ExternalSilkDecoderException(null, null, null, "The external SILK decoder could not be started.");
            }
        }
        catch (Win32Exception exception)
        {
            throw new ExternalSilkDecoderException(null, null, exception.Message, "The external SILK decoder could not be started.", exception);
        }

        var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(standardOutputTask, standardErrorTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            await ObserveTasksAsync(standardOutputTask, standardErrorTask).ConfigureAwait(false);
            throw;
        }

        return new DecoderProcessResult(process.ExitCode, standardOutputTask.Result, standardErrorTask.Result);
    }

    private static async Task CopyInputAsync(string inputPath, string temporaryInputPath, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(
            inputPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(
            temporaryInputPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        await input.CopyToAsync(output, BufferSize, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between HasExited and Kill.
        }
        catch (Win32Exception)
        {
            // Preserve cancellation; process teardown remains best effort.
        }
    }

    private static async Task ObserveTasksAsync(params Task<string>[] tasks)
    {
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is rethrown by the caller after process teardown.
        }
        catch (Exception)
        {
            // A terminated process can close its redirected stream abruptly.
            // The caller rethrows its cancellation after process teardown.
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
            // Preserve the primary decoder result or failure.
        }
        catch (UnauthorizedAccessException)
        {
            // Preserve the primary decoder result or failure.
        }
    }

    private sealed record DecoderProcessResult(int ExitCode, string StandardOutput, string StandardError);
}

/// <summary>
/// Captures diagnostics emitted by the configured external decoder without
/// exposing an arbitrary command execution surface to callers.
/// </summary>
public sealed class ExternalSilkDecoderException : IOException
{
    public ExternalSilkDecoderException(
        int? exitCode,
        string? standardOutput,
        string? standardError,
        string message,
        Exception? innerException = null)
        : base(BuildMessage(message, exitCode, standardError), innerException)
    {
        ExitCode = exitCode;
        StandardOutput = standardOutput;
        StandardError = standardError;
    }

    public int? ExitCode { get; }

    public string? StandardOutput { get; }

    public string? StandardError { get; }

    private static string BuildMessage(string message, int? exitCode, string? standardError)
    {
        var suffix = exitCode is null ? string.Empty : $" Exit code: {exitCode}.";
        var diagnostic = string.IsNullOrWhiteSpace(standardError)
            ? string.Empty
            : $" Stderr: {Truncate(standardError)}";
        return message + suffix + diagnostic;
    }

    private static string Truncate(string value)
        => value.Length <= 4_096 ? value : value[..4_096] + "…";
}
