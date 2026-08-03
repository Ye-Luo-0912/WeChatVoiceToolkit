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
public sealed class DecoderVoiceDurationResolver : IVersionedVoiceDurationResolver, IVoiceDecoderIdentity, IVoiceStreamDurationResolver, IAsyncDisposable
{
    public const string CurrentDecoderVersion = "silk-wav-decoder-v1";

    private readonly IVoiceDecoder _decoder;
    private readonly ITemporaryFileCleanupQueue? _cleanupQueue;
    private readonly SemaphoreSlim _decoderGate = new(1, 1);

    public DecoderVoiceDurationResolver(
        IVoiceDecoder decoder,
        ITemporaryFileCleanupQueue? cleanupQueue = null)
    {
        _decoder = decoder ?? throw new ArgumentNullException(nameof(decoder));
        _cleanupQueue = cleanupQueue;
    }

    public string DecoderVersion => _decoder is IVoiceDecoderIdentity identity
        ? identity.DecoderIdentity
        : CurrentDecoderVersion;

    public string DecoderIdentity => DecoderVersion;

    public async Task<long?> ResolveAsync(IVoiceCatalog catalog, VoiceRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(record);
        if (record.PayloadState != VoicePayloadState.Linked || record.PayloadLocator is null)
            return null;

        await using var input = await catalog.OpenPayloadAsync(record.PayloadLocator, cancellationToken).ConfigureAwait(false);
        return await ResolveAsync(input, cancellationToken).ConfigureAwait(false);
    }

    public async Task<long?> ResolveAsync(Stream payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (!payload.CanRead)
        {
            throw new InvalidDataException("The duration resolver requires a readable payload stream.");
        }

        await _decoderGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var directory = Path.Combine(Path.GetTempPath(), "wechatvoice-duration");
        var token = Guid.NewGuid().ToString("N");
        var outputPath = Path.Combine(directory, token + ".wav");
        try
        {
            Directory.CreateDirectory(directory);
            await using (var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await _decoder.DecodeAsync(payload, output, cancellationToken).ConfigureAwait(false);
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
            try
            {
                EnqueueCleanupIfNeeded(outputPath);
            }
            finally
            {
                _decoderGate.Release();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _decoderGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_decoder is IAsyncDisposable disposable)
            {
                await disposable.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _decoderGate.Release();
            _decoderGate.Dispose();
        }
    }

    private void EnqueueCleanupIfNeeded(string path)
    {
        var failure = TryDelete(path);
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
            // Cleanup diagnostics must not replace the duration result.
        }
    }

    private static CleanupDiagnostic? TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            File.Delete(path);
            return File.Exists(path)
                ? new CleanupDiagnostic("duration-wav", "delete-still-present", nameof(IOException))
                : null;
        }
        catch (IOException exception)
        {
            return new CleanupDiagnostic("duration-wav", "delete-failed", exception.GetType().Name);
        }
        catch (UnauthorizedAccessException exception)
        {
            return new CleanupDiagnostic("duration-wav", "delete-failed", exception.GetType().Name);
        }
    }
}
