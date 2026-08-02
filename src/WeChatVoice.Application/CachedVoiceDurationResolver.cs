using System.Buffers;
using System.Security.Cryptography;
using WeChatVoice.Core.Models;
using WeChatVoice.Core.Ports;

namespace WeChatVoice.Application;

/// <summary>
/// Adds a content-addressed duration cache around the configured decoder.
/// When an adapter has not performed a deep payload scan, the resolver hashes
/// the verified payload stream before consulting the cache. A cache miss then
/// invokes the decoder normally; the extra hash pass is paid only for the
/// first uncached shallow duration scan.
/// </summary>
public sealed class CachedVoiceDurationResolver : IVersionedVoiceDurationResolver
{
    private const int BufferSize = 128 * 1024;
    private readonly IVoiceDurationResolver _inner;
    private readonly IVoiceDurationCache _cache;

    public CachedVoiceDurationResolver(
        IVoiceDurationResolver inner,
        IVoiceDurationCache cache)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        DecoderVersion = inner is IVersionedVoiceDurationResolver versioned
            ? versioned.DecoderVersion
            : cache.DecoderVersion;
        if (!string.Equals(DecoderVersion, cache.DecoderVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException("The duration resolver and cache decoder versions do not match.", nameof(cache));
        }
    }

    public string DecoderVersion { get; }

    public async Task<long?> ResolveAsync(
        IVoiceCatalog catalog,
        VoiceRecord record,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();

        if (record.SourceStableKey is null || record.PayloadState != VoicePayloadState.Linked || record.PayloadLocator is null)
        {
            return await _inner.ResolveAsync(catalog, record, cancellationToken).ConfigureAwait(false);
        }

        var payloadSha256 = NormalizeSha256(record.PayloadSha256);
        if (payloadSha256 is null)
        {
            try
            {
                payloadSha256 = await ComputePayloadSha256Async(catalog, record.PayloadLocator, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                // Cache lookup is an optimization. Let the configured
                // resolver produce the authoritative duration when hashing
                // cannot be performed independently.
                return await _inner.ResolveAsync(catalog, record, cancellationToken).ConfigureAwait(false);
            }
        }

        var key = new VoiceDurationCacheKey(record.SourceStableKey, payloadSha256, DecoderVersion);
        long? cached;
        try
        {
            cached = await _cache.TryGetAsync(key, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            cached = null;
        }

        if (cached is > 0)
        {
            return cached;
        }

        var resolved = await _inner.ResolveAsync(catalog, record, cancellationToken).ConfigureAwait(false);
        if (resolved is > 0)
        {
            try
            {
                await _cache.StoreAsync(
                    new VoiceDurationCacheEntry(key, resolved.Value, DateTimeOffset.UtcNow),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                // A read-only or temporarily unavailable cache must not turn
                // a valid duration calculation into a failed scan.
            }
        }

        return resolved;
    }

    private static async Task<string> ComputePayloadSha256Async(
        IVoiceCatalog catalog,
        VoicePayloadLocator locator,
        CancellationToken cancellationToken)
    {
        await using var input = await catalog.OpenPayloadAsync(locator, cancellationToken).ConfigureAwait(false);
        if (!input.CanRead)
        {
            throw new InvalidDataException("The voice catalog returned a non-readable payload stream.");
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                hash.AppendData(buffer, 0, read);
            }

            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static string? NormalizeSha256(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 64)
        {
            return null;
        }

        foreach (var character in value)
        {
            if (!Uri.IsHexDigit(character))
            {
                return null;
            }
        }

        return value.ToLowerInvariant();
    }
}
