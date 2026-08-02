using WeChatVoice.Core.Models;
using WeChatVoice.Core.Ports;
using WeChatVoice.Infrastructure.Export;

namespace WeChatVoice.Infrastructure.Audio;

/// <summary>
/// Uses the already configured decoder for optional duration analysis. Output
/// is staged in the OS temp directory and is never added to the export tree.
/// A single resolver instance serializes decoder calls, avoiding unbounded
/// process and temporary-file pressure during a scan.
/// </summary>
public sealed class DecoderVoiceDurationResolver : IVoiceDurationResolver, IAsyncDisposable
{
    private readonly IVoiceDecoder _decoder;
    private readonly SemaphoreSlim _decoderGate = new(1, 1);

    public DecoderVoiceDurationResolver(IVoiceDecoder decoder)
        => _decoder = decoder ?? throw new ArgumentNullException(nameof(decoder));

    public async Task<long?> ResolveAsync(IVoiceCatalog catalog, VoiceRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(record);
        if (record.PayloadState != VoicePayloadState.Linked || record.PayloadLocator is null)
            return null;

        await _decoderGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var directory = Path.Combine(Path.GetTempPath(), "wechatvoice-duration");
        var token = Guid.NewGuid().ToString("N");
        var outputPath = Path.Combine(directory, token + ".wav");
        try
        {
            Directory.CreateDirectory(directory);
            await using var input = await catalog.OpenPayloadAsync(record.PayloadLocator, cancellationToken).ConfigureAwait(false);
            await using (var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await _decoder.DecodeAsync(input, output, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            return await WavFileValidator.TryReadDurationMsAsync(outputPath, cancellationToken).ConfigureAwait(false);
        }
        catch (ExternalSilkDecoderException)
        {
            return null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
        finally
        {
            TryDelete(outputPath);
            _decoderGate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        _decoderGate.Dispose();
        return ValueTask.CompletedTask;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
