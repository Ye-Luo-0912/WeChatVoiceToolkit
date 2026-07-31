namespace WeChatVoice.Core.Ports;

/// <summary>
/// Converts a persisted SILK file to a WAV file without modifying the input.
/// </summary>
public interface IVoiceDecoder
{
    Task DecodeAsync(string inputPath, string outputPath, CancellationToken cancellationToken);
}
