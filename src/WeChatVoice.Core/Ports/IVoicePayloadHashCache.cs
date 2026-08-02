using WeChatVoice.Core.Models;

namespace WeChatVoice.Core.Ports;

/// <summary>
/// Deep-scan cache. It is an optimization only; the database group
/// fingerprint in the key comes from the verified Workspace boundary.
/// </summary>
public interface IVoicePayloadHashCache : IAsyncDisposable
{
    ValueTask<string?> TryGetAsync(
        VoicePayloadHashCacheKey key,
        CancellationToken cancellationToken);

    ValueTask StoreAsync(
        VoicePayloadHashCacheEntry entry,
        CancellationToken cancellationToken);
}
