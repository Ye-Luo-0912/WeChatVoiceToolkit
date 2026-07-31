using WeChatVoice.Core.Models;

namespace WeChatVoice.Core.Ports;

/// <summary>
/// Supplies voice metadata and independently-opened payload streams.
/// Each returned stream is owned and disposed by the caller.
/// </summary>
public interface IVoiceSource
{
    IAsyncEnumerable<VoiceMessage> QueryAsync(VoiceQuery query, CancellationToken cancellationToken);

    ValueTask<Stream> OpenPayloadAsync(VoiceMessage message, CancellationToken cancellationToken);
}
