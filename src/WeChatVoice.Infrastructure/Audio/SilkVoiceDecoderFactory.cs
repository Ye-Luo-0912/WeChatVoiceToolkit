using WeChatVoice.Core.Ports;
using WeChatVoice.Infrastructure.Export;

namespace WeChatVoice.Infrastructure.Audio;

/// <summary>
/// Creates a SILK decoder for the requested sample rate using the same
/// discovery order as duration analysis: the persistent user configuration
/// first, then the advanced environment variable. Returns null when no decoder
/// is configured so the caller can fall back to a non-decoder path.
/// </summary>
public sealed class SilkVoiceDecoderFactory : IVoiceDecoderFactory
{
    private readonly DecoderConfigurationStore? _store;
    private readonly Func<string?>? _environment;

    public SilkVoiceDecoderFactory(DecoderConfigurationStore? store = null, Func<string?>? environment = null)
    {
        _store = store;
        _environment = environment;
    }

    public IVoiceDecoder? Create(int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        var inspector = new DecoderStatusInspector(_store, _environment);
        var workerPath = inspector.DiscoverWorkerPath();
        if (!string.IsNullOrWhiteSpace(workerPath) && File.Exists(workerPath))
        {
            return new ExternalSilkDecoderWorker(workerPath, sampleRate);
        }

        var path = _environment?.Invoke() ?? Environment.GetEnvironmentVariable(DecoderStatusInspector.LegacyEnvironmentVariable);
        return string.IsNullOrWhiteSpace(path) || !File.Exists(path)
            ? null
            : new ExternalSilkDecoder(path, sampleRate);
    }
}