namespace WeChatVoice.Core.Ports;

/// <summary>
/// Creates a SILK decoder for a requested sample rate. The dataset WAV build
/// needs a decoder configured for the profile's sample rate, so hosts cannot
/// reuse a single fixed-rate instance; this factory narrows the creation
/// surface and keeps decoder discovery in the composition root.
/// </summary>
public interface IVoiceDecoderFactory
{
    /// <summary>
    /// Creates a decoder for the requested PCM sample rate, or null when no
    /// decoder is configured. The returned object also implements
    /// <see cref="IVoiceDecoderIdentity"/> so the caller can record which
    /// decoder produced a derived output.
    /// </summary>
    IVoiceDecoder? Create(int sampleRate);
}