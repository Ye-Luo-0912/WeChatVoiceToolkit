namespace WeChatVoice.Core.Models;

/// <summary>
/// A deep-scan hash is reusable only for the same stable message, payload
/// length, and verified database group. The group fingerprint is the
/// cryptographic anchor that prevents a cache entry from crossing a changed
/// Workspace.
/// </summary>
public sealed record VoicePayloadHashCacheKey
{
    public VoicePayloadHashCacheKey(
        string sourceStableKey,
        long payloadByteLength,
        string databaseFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceStableKey);
        if (payloadByteLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(payloadByteLength));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(databaseFingerprint);
        SourceStableKey = sourceStableKey;
        PayloadByteLength = payloadByteLength;
        DatabaseFingerprint = databaseFingerprint;
    }

    public string SourceStableKey { get; }

    public long PayloadByteLength { get; }

    public string DatabaseFingerprint { get; }
}

public sealed record VoicePayloadHashCacheEntry
{
    public VoicePayloadHashCacheEntry(
        VoicePayloadHashCacheKey key,
        string payloadSha256,
        DateTimeOffset updatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadSha256);
        if (payloadSha256.Length != 64 || !payloadSha256.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("A payload hash cache entry requires a SHA-256 value.", nameof(payloadSha256));
        }

        Key = key;
        PayloadSha256 = payloadSha256.ToLowerInvariant();
        UpdatedAtUtc = updatedAtUtc.ToUniversalTime();
    }

    public VoicePayloadHashCacheKey Key { get; }

    public string PayloadSha256 { get; }

    public DateTimeOffset UpdatedAtUtc { get; }
}
