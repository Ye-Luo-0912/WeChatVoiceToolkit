using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WeChatVoice.Core.Ports;
using WeChatVoice.Infrastructure.Export;

namespace WeChatVoice.Infrastructure.Audio;

/// <summary>
/// Resident decoder boundary for reviewed decoder executables that implement
/// <c>wechatvoice-decoder-jsonl-v1</c>. The host starts one process lazily and
/// sends one bounded JSONL request at a time. Payloads are exposed only through
/// short-lived staging files, never through caller paths or arbitrary commands.
///
/// Worker stdout is reserved for protocol responses. Diagnostics must be sent
/// to stderr, which is drained with a fixed memory budget.
/// </summary>
public sealed class ExternalSilkDecoderWorker : IVoiceDecoder, IVoiceDecoderIdentity, IAsyncDisposable
{
    public const string ProtocolVersion = "wechatvoice-decoder-jsonl-v1";

    private const int BufferSize = 128 * 1024;
    private const int MaximumDiagnosticCharacters = 64 * 1024;
    private const int MaximumProtocolLineCharacters = 64 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _executablePath;
    private readonly int _sampleRate;
    private readonly string _workingDirectory;
    private readonly ITemporaryFileCleanupQueue? _cleanupQueue;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lazy<string> _decoderIdentity;
    private Process? _process;
    private StreamWriter? _requests;
    private StreamReader? _responses;
    private Task? _stderrDrain;
    private readonly char[] _responseBuffer = new char[4096];
    private int _responseBufferOffset;
    private int _responseBufferCount;
    private bool _disposed;

    public ExternalSilkDecoderWorker(
        string executablePath,
        int sampleRate = 24000,
        string? workingDirectory = null,
        ITemporaryFileCleanupQueue? cleanupQueue = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

        _executablePath = Path.GetFullPath(executablePath);
        if (!File.Exists(_executablePath))
        {
            throw new FileNotFoundException("The configured SILK decoder worker executable was not found.", _executablePath);
        }

        _sampleRate = sampleRate;
        _workingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
            ? Path.GetDirectoryName(_executablePath)!
            : Path.GetFullPath(workingDirectory);
        if (!Directory.Exists(_workingDirectory))
        {
            throw new DirectoryNotFoundException("The configured SILK decoder worker directory was not found.");
        }

        _cleanupQueue = cleanupQueue;
        _decoderIdentity = new Lazy<string>(ComputeDecoderIdentity, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string DecoderIdentity => _decoderIdentity.Value;

    public async Task DecodeAsync(Stream input, Stream output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        if (!input.CanRead || !output.CanWrite)
        {
            throw new InvalidDataException("The decoder worker requires a readable input and writable output stream.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var directory = Path.Combine(Path.GetTempPath(), "wechatvoice-decoder-worker");
        var token = Guid.NewGuid().ToString("N");
        var inputPath = Path.Combine(directory, token + ".input.silk");
        var outputPath = Path.Combine(directory, token + ".output.wav");
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Directory.CreateDirectory(directory);
            await CopyToFileAsync(input, inputPath, cancellationToken).ConfigureAwait(false);
            await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);

            var request = new WorkerRequest(
                ProtocolVersion,
                token,
                inputPath,
                outputPath,
                _sampleRate);
            await _requests!.WriteLineAsync(JsonSerializer.Serialize(request, JsonOptions)).ConfigureAwait(false);
            await _requests.FlushAsync(cancellationToken).ConfigureAwait(false);

            var responseLine = await ReadResponseLineAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new ExternalSilkDecoderException(null, null, null, "The decoder worker closed its protocol output.");
            WorkerResponse response;
            try
            {
                response = JsonSerializer.Deserialize<WorkerResponse>(responseLine, JsonOptions)
                    ?? throw new JsonException("The decoder worker returned an empty response.");
            }
            catch (JsonException exception)
            {
                throw new ExternalSilkDecoderException(null, responseLine, null, "The decoder worker returned malformed JSONL.", exception);
            }

            if (!string.Equals(response.Protocol, ProtocolVersion, StringComparison.Ordinal)
                || !string.Equals(response.RequestId, token, StringComparison.Ordinal))
            {
                throw new ExternalSilkDecoderException(null, responseLine, null, "The decoder worker response identity did not match the request.");
            }

            if (!response.Success)
            {
                throw new ExternalSilkDecoderException(
                    response.ExitCode,
                    null,
                    response.Error,
                    "The resident SILK decoder worker rejected the request.");
            }

            if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            {
                throw new ExternalSilkDecoderException(
                    response.ExitCode,
                    null,
                    response.Error,
                    "The resident SILK decoder worker completed without producing a WAV file.");
            }

            await using var decoded = new FileStream(
                outputPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await decoded.CopyToAsync(output, BufferSize, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await ResetWorkerAsync().ConfigureAwait(false);
            throw;
        }
        catch (ExternalSilkDecoderException)
        {
            await ResetWorkerAsync().ConfigureAwait(false);
            throw;
        }
        catch (IOException)
        {
            await ResetWorkerAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            try
            {
                EnqueueCleanupIfNeeded(inputPath, "decoder-worker-input");
            }
            finally
            {
                try
                {
                    EnqueueCleanupIfNeeded(outputPath, "decoder-worker-output");
                }
                finally
                {
                    _gate.Release();
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
            await ResetWorkerAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (_process is { HasExited: false })
        {
            return;
        }

        await ResetWorkerAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = new ProcessStartInfo
        {
            FileName = _executablePath,
            WorkingDirectory = _workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("--worker");
        startInfo.ArgumentList.Add("--protocol");
        startInfo.ArgumentList.Add(ProtocolVersion);
        startInfo.ArgumentList.Add("--sample-rate");
        startInfo.ArgumentList.Add(_sampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new ExternalSilkDecoderException(null, null, null, "The resident SILK decoder worker could not be started.");
            }
        }
        catch (Win32Exception exception)
        {
            process.Dispose();
            throw new ExternalSilkDecoderException(null, null, exception.Message, "The resident SILK decoder worker could not be started.", exception);
        }

        _process = process;
        _requests = process.StandardInput;
        _responses = process.StandardOutput;
        _stderrDrain = DrainStandardErrorAsync(process.StandardError);
    }

    private async Task ResetWorkerAsync()
    {
        var process = _process;
        _process = null;
        _requests = null;
        _responses = null;
        var stderrDrain = _stderrDrain;
        _stderrDrain = null;
        _responseBufferOffset = 0;
        _responseBufferCount = 0;

        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
        }

        process.Dispose();
        if (stderrDrain is not null)
        {
            try
            {
                await stderrDrain.ConfigureAwait(false);
            }
            catch (Exception) when (stderrDrain.IsFaulted)
            {
            }
        }
    }

    private string ComputeDecoderIdentity()
    {
        using var stream = new FileStream(_executablePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) != 0)
        {
            hash.AppendData(buffer, 0, read);
        }

        var executableHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        var contract = string.Join(
            "\n",
            executableHash,
            ProtocolVersion,
            _sampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
            WavFileValidator.ContractVersion);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(contract))).ToLowerInvariant();
    }

    private static async Task CopyToFileAsync(Stream input, string path, CancellationToken cancellationToken)
    {
        await using var output = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await input.CopyToAsync(output, BufferSize, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> ReadResponseLineAsync(CancellationToken cancellationToken)
    {
        var reader = _responses ?? throw new InvalidOperationException("The decoder worker protocol is not connected.");
        var line = new StringBuilder(Math.Min(MaximumProtocolLineCharacters, _responseBuffer.Length));
        while (true)
        {
            if (_responseBufferOffset >= _responseBufferCount)
            {
                _responseBufferOffset = 0;
                _responseBufferCount = await reader.ReadAsync(_responseBuffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (_responseBufferCount == 0)
                {
                    return line.Length == 0 ? null : line.ToString();
                }
            }

            var available = _responseBuffer.AsSpan(_responseBufferOffset, _responseBufferCount - _responseBufferOffset);
            var newline = available.IndexOf('\n');
            var contentLength = newline >= 0 ? newline : available.Length;
            if (newline >= 0 && contentLength > 0 && available[contentLength - 1] == '\r')
            {
                contentLength--;
            }

            if (line.Length > MaximumProtocolLineCharacters - contentLength)
            {
                throw new ExternalSilkDecoderException(
                    null,
                    null,
                    null,
                    "The decoder worker protocol response exceeded the maximum line length.");
            }

            line.Append(available[..contentLength]);
            _responseBufferOffset += newline >= 0 ? newline + 1 : available.Length;
            if (newline >= 0)
            {
                return line.ToString();
            }
        }
    }

    private static async Task DrainStandardErrorAsync(StreamReader reader)
    {
        var buffer = new char[4096];
        var retained = 0;
        while (true)
        {
            var read = await reader.ReadAsync(buffer).ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }

            retained = Math.Min(MaximumDiagnosticCharacters, retained + read);
        }
    }

    private void EnqueueCleanupIfNeeded(string path, string resourceKind)
    {
        var failure = TryDelete(path, resourceKind);
        if (failure is null || _cleanupQueue is null)
        {
            return;
        }

        try
        {
            _cleanupQueue.Enqueue(path, failure);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Cleanup is diagnostic-only and must never replace the decoder
            // result or prevent the gate from being released.
        }
    }

    private static CleanupDiagnostic? TryDelete(string path, string resourceKind)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            File.Delete(path);
            return File.Exists(path)
                ? new CleanupDiagnostic(resourceKind, "delete-still-present", nameof(IOException))
                : null;
        }
        catch (IOException exception)
        {
            return new CleanupDiagnostic(resourceKind, "delete-failed", exception.GetType().Name);
        }
        catch (UnauthorizedAccessException exception)
        {
            return new CleanupDiagnostic(resourceKind, "delete-failed", exception.GetType().Name);
        }
        catch (Exception exception)
        {
            return new CleanupDiagnostic(resourceKind, "delete-failed", exception.GetType().Name);
        }
    }

    private sealed record WorkerRequest(
        string Protocol,
        string RequestId,
        string InputPath,
        string OutputPath,
        int SampleRate);

    private sealed record WorkerResponse(
        string Protocol,
        string RequestId,
        bool Success,
        int? ExitCode = null,
        string? Error = null);
}
