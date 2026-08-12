using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using WeChatVoice.Core.Ports;
using WeChatVoice.Infrastructure.Export;

namespace WeChatVoice.Infrastructure.Audio;

/// <summary>
/// Runs the bundled MIT-licensed WeChat SILK v3 command-line decoder. The
/// bundled program has a deliberately tiny, fixed contract (input SILK,
/// output signed-16-bit PCM), so callers never provide an arbitrary command.
/// PCM is wrapped in a validated RIFF/WAVE stream for the existing decoder
/// pipeline.
/// </summary>
public sealed class BundledSilkDecoder : IVoiceDecoder, IVoiceDecoderIdentity
{
    private const int BufferSize = 128 * 1024;
    private const int MaximumFrameBytes = 1024;
    private const long MaximumInputBytes = 64L * 1024 * 1024;
    private const int MaximumDiagnosticCharacters = 64 * 1024;
    private const string ProtocolVersion = "wechatvoice-bundled-silk-cli-v1";
    private readonly string _executablePath;
    private readonly int _sampleRate;
    private readonly ITemporaryFileCleanupQueue? _cleanupQueue;
    private readonly Lazy<string> _decoderIdentity;

    public BundledSilkDecoder(
        string executablePath,
        int sampleRate = 24000,
        ITemporaryFileCleanupQueue? cleanupQueue = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        _executablePath = Path.GetFullPath(executablePath);
        _sampleRate = sampleRate;
        _cleanupQueue = cleanupQueue;
        _decoderIdentity = new Lazy<string>(ComputeDecoderIdentity, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string DecoderIdentity => _decoderIdentity.Value;

    public async Task DecodeAsync(Stream input, Stream output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(_executablePath))
        {
            throw new FileNotFoundException("The bundled SILK decoder is not available.");
        }

        var directory = Path.Combine(Path.GetTempPath(), "wechatvoice-bundled-decoder");
        Directory.CreateDirectory(directory);
        var token = Guid.NewGuid().ToString("N");
        var inputPath = Path.Combine(directory, token + ".input.silk");
        var pcmPath = Path.Combine(directory, token + ".output.pcm");
        try
        {
            await using (var stagedInput = new FileStream(inputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await input.CopyToAsync(stagedInput, BufferSize, cancellationToken).ConfigureAwait(false);
                await stagedInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            await ValidateSilkInputAsync(inputPath, cancellationToken).ConfigureAwait(false);

            var result = await RunDecoderAsync(inputPath, pcmPath, cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                throw new ExternalSilkDecoderException(result.ExitCode, result.StandardOutput, result.StandardError, "The bundled SILK decoder returned a non-zero exit code.");
            }

            if (!File.Exists(pcmPath) || new FileInfo(pcmPath).Length is 0)
            {
                throw new ExternalSilkDecoderException(result.ExitCode, result.StandardOutput, result.StandardError, "The bundled SILK decoder produced no PCM audio.");
            }

            await WriteWavAsync(pcmPath, output, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            EnqueueCleanupIfNeeded(inputPath, "bundled-decoder-input");
            EnqueueCleanupIfNeeded(pcmPath, "bundled-decoder-output");
        }
    }

    private static async Task ValidateSilkInputAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is 0 or > MaximumInputBytes)
        {
            throw new InvalidDataException("The SILK payload is empty or exceeds the bundled decoder input limit.");
        }

        var prefix = new byte[10];
        var read = await stream.ReadAsync(prefix, cancellationToken).ConfigureAwait(false);
        var hasWrappedHeader = read >= 10 && prefix[0] == 0x02 && prefix[1..10].SequenceEqual("#!SILK_V3"u8);
        var hasPlainHeader = read >= 9 && prefix[..9].SequenceEqual("#!SILK_V3"u8);
        if (!hasWrappedHeader && !hasPlainHeader)
        {
            throw new InvalidDataException("The SILK payload header is not recognized.");
        }

        stream.Position = hasWrappedHeader ? 10 : 9;
        var lengthBytes = new byte[2];
        while (stream.Position < stream.Length)
        {
            if (await ReadExactlyAsync(stream, lengthBytes, cancellationToken).ConfigureAwait(false) != lengthBytes.Length)
            {
                throw new InvalidDataException("The SILK payload contains a truncated frame length.");
            }

            var frameLength = BinaryPrimitives.ReadInt16LittleEndian(lengthBytes);
            if (frameLength <= 0 || frameLength > MaximumFrameBytes || stream.Length - stream.Position < frameLength)
            {
                throw new InvalidDataException("The SILK payload contains an invalid frame length.");
            }

            stream.Position += frameLength;
        }
    }

    private static async Task<int> ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[total..], cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
        }

        return total;
    }

    private async Task<DecoderProcessResult> RunDecoderAsync(string inputPath, string pcmPath, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _executablePath,
            WorkingDirectory = Path.GetDirectoryName(_executablePath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(inputPath);
        startInfo.ArgumentList.Add(pcmPath);
        startInfo.ArgumentList.Add("-Fs_API");
        startInfo.ArgumentList.Add(_sampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-quiet");

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new ExternalSilkDecoderException(null, null, null, "The bundled SILK decoder could not be started.");
            }
        }
        catch (Win32Exception exception)
        {
            throw new ExternalSilkDecoderException(null, null, exception.Message, "The bundled SILK decoder could not be started.", exception);
        }

        var stdout = ReadBoundedTextAsync(process.StandardOutput, cancellationToken);
        var stderr = ReadBoundedTextAsync(process.StandardError, cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (Exception) when (process.HasExited)
            {
            }
            throw;
        }

        return new DecoderProcessResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    private async Task WriteWavAsync(string pcmPath, Stream output, CancellationToken cancellationToken)
    {
        var length = new FileInfo(pcmPath).Length;
        if (length is 0 or > uint.MaxValue || (length & 1) != 0)
        {
            throw new InvalidDataException("The bundled SILK decoder produced invalid PCM output.");
        }

        var header = new byte[44];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(header, 0);
        BitConverter.GetBytes((uint)(36 + length)).CopyTo(header, 4);
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(header, 8);
        BitConverter.GetBytes(16u).CopyTo(header, 16);
        BitConverter.GetBytes((ushort)1).CopyTo(header, 20);
        BitConverter.GetBytes((ushort)1).CopyTo(header, 22);
        BitConverter.GetBytes((uint)_sampleRate).CopyTo(header, 24);
        BitConverter.GetBytes((uint)(_sampleRate * 2)).CopyTo(header, 28);
        BitConverter.GetBytes((ushort)2).CopyTo(header, 32);
        BitConverter.GetBytes((ushort)16).CopyTo(header, 34);
        Encoding.ASCII.GetBytes("data").CopyTo(header, 36);
        BitConverter.GetBytes((uint)length).CopyTo(header, 40);
        await output.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await using var pcm = new FileStream(pcmPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await pcm.CopyToAsync(output, BufferSize, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ReadBoundedTextAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var builder = new StringBuilder();
        var truncated = false;
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            var remaining = MaximumDiagnosticCharacters - builder.Length;
            if (remaining > 0) builder.Append(buffer, 0, Math.Min(remaining, read));
            if (read > remaining) truncated = true;
        }
        return truncated ? builder + "…[truncated]" : builder.ToString();
    }

    private string ComputeDecoderIdentity()
    {
        using var stream = new FileStream(_executablePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        var contract = string.Join("\n", hash, ProtocolVersion, _sampleRate, WavFileValidator.ContractVersion);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(contract))).ToLowerInvariant();
    }

    private void EnqueueCleanupIfNeeded(string path, string resourceKind)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _cleanupQueue?.Enqueue(path, new CleanupDiagnostic(resourceKind, "delete-failed", exception.GetType().Name));
        }
    }

    private sealed record DecoderProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
