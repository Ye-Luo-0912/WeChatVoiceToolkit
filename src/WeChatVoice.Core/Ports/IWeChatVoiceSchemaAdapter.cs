using WeChatVoice.Core.Models;

namespace WeChatVoice.Core.Ports;

/// <summary>
/// Version-specific, read-only mapping from an inspected database schema to
/// voice-message metadata. Implementations must not guess unsupported schemas.
/// </summary>
public interface IWeChatVoiceSchemaAdapter
{
    string Id { get; }

    bool CanHandle(SchemaSnapshot schema);

    IAsyncEnumerable<VoiceMessage> QueryAsync(
        SchemaSnapshot schema,
        VoiceQuery query,
        CancellationToken cancellationToken);
}
