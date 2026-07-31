namespace WeChatVoice.Core.Ports;

/// <summary>
/// Converts a SILK payload stream to a WAV output stream without exposing
/// physical export paths to the application layer.
/// </summary>
public interface IVoiceDecoder
{
    Task DecodeAsync(Stream input, Stream output, CancellationToken cancellationToken);
}
