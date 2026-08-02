using System.Buffers;
using System.Security.Cryptography;
using WeChatVoice.Core.Models;
using WeChatVoice.Core.Ports;

namespace WeChatVoice.Application;

/// <summary>
/// Resolves deep payload hashes through a verified catalog and reuses only
/// entries anchored to the catalog's current database fingerprints.
/// </summary>
public sealed class CachedVoicePayloadHashResolver
{
    private const int BufferSize = 128 * 1024;
    private readonly IVoicePayloadHashCache _cache;

    public CachedVoicePayloadHashResolver(IVoicePayloadHashCache cache)
        => _cache = cache ?? throw new ArgumentNullException(nameof(cache));

    public async Task<string?> ResolveAsync(
        IVoiceCatalog catalog,
        VoiceRecord record,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(record);
        if (record.PayloadState != VoicePayloadState.Linked
            || record.PayloadLocator is null
            || record.PayloadByteLength is not > 0
            || record.SourceStableKey is null)
        {
            return null;
        }

        var databaseFingerprint = string.Join("|", catalog.Context.DatabaseFingerprints);
        if (string.IsNullOrWhiteSpace(databaseFingerprint))
        {
            return null;
        }

        var key = new VoicePayloadHashCacheKey(
            record.SourceStableKey,
            record.PayloadByteLength.Value,
            databaseFingerprint);
        string? cached = null;
        try
        {
            cached = await _cache.TryGetAsync(key, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // A cache is never allowed to turn a verified catalog read into a
            // failure. The current payload is still hashed below.
        }

        if (!string.IsNullOrWhiteSpace(cached))
        {
            return cached;
        }

        await using var input = await catalog.OpenPayloadAsync(record.PayloadLocator, cancellationToken).ConfigureAwait(false);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            long length = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                length = checked(length + read);
                hash.AppendData(buffer, 0, read);
            }

            if (length != record.PayloadByteLength.Value)
            {
                throw new InvalidDataException("The payload length changed while calculating its deep-scan hash.");
            }

            var resolved = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            try
            {
                await _cache.StoreAsync(
                    new VoicePayloadHashCacheEntry(key, resolved, DateTimeOffset.UtcNow),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                // Cache persistence is optional; the verified hash remains
                // authoritative for this scan.
            }

            return resolved;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }
}
