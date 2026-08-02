namespace WeChatVoice.Core.Models;

/// <summary>
/// The immutable identity used by the duration cache. A duration is reusable
/// only when the source record, the actual SILK bytes, and the decoder
/// implementation are all the same.
/// </summary>
public sealed record VoiceDurationCacheKey
{
    public VoiceDurationCacheKey(
        string sourceStableKey,
        string payloadSha256,
        string decoderVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceStableKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(decoderVersion);
        SourceStableKey = sourceStableKey;
        PayloadSha256 = payloadSha256;
        DecoderVersion = decoderVersion;
    }

    public string SourceStableKey { get; }

    public string PayloadSha256 { get; }

    public string DecoderVersion { get; }
}

public sealed record VoiceDurationCacheEntry
{
    public VoiceDurationCacheEntry(
        VoiceDurationCacheKey key,
        long durationMs,
        DateTimeOffset updatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (durationMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(durationMs), "A cached duration must be positive.");
        }

        Key = key;
        DurationMs = durationMs;
        UpdatedAtUtc = updatedAtUtc.ToUniversalTime();
    }

    public VoiceDurationCacheKey Key { get; }

    public long DurationMs { get; }

    public DateTimeOffset UpdatedAtUtc { get; }
}
