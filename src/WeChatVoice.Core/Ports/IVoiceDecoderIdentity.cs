namespace WeChatVoice.Core.Ports;

/// <summary>
/// Cryptographic identity of the decoder and its output-validation contract.
/// Duration caches must be partitioned by this value so replacing a decoder
/// binary or changing its audio contract cannot reuse stale measurements.
/// </summary>
public interface IVoiceDecoderIdentity
{
    string DecoderIdentity { get; }
}
