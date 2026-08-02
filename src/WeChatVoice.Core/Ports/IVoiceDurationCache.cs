using WeChatVoice.Core.Models;

namespace WeChatVoice.Core.Ports;

/// <summary>
/// Durable, local-only duration cache. Implementations must treat entries as
/// an optimization: malformed or unavailable cache data must never weaken the
/// verified source/export boundary.
/// </summary>
public interface IVoiceDurationCache : IAsyncDisposable
{
    string DecoderVersion { get; }

    ValueTask<long?> TryGetAsync(
        VoiceDurationCacheKey key,
        CancellationToken cancellationToken);

    ValueTask StoreAsync(
        VoiceDurationCacheEntry entry,
        CancellationToken cancellationToken);
}

/// <summary>
/// Optional explicit decoder version used to invalidate cached durations when
/// the decoder or its output contract changes.
/// </summary>
public interface IVersionedVoiceDurationResolver : IVoiceDurationResolver
{
    string DecoderVersion { get; }
}
