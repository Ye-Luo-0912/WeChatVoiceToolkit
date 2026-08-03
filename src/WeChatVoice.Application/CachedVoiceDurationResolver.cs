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
    private readonly ITemporaryFileCleanupQueue? _cleanupQueue;

    public CachedVoiceDurationResolver(
        IVoiceDurationResolver inner,
        IVoiceDurationCache cache,
        ITemporaryFileCleanupQueue? cleanupQueue = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _cleanupQueue = cleanupQueue;
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
            return await ResolveUnknownHashAsync(catalog, record, cancellationToken).ConfigureAwait(false);
        }

        return await ResolveWithKnownHashAsync(catalog, record, payloadSha256, cancellationToken).ConfigureAwait(false);
    }

    private async Task<long?> ResolveUnknownHashAsync(
        IVoiceCatalog catalog,
        VoiceRecord record,
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Path.GetTempPath(), "wechatvoice-duration-input");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".silk");
        try
        {
            string payloadSha256;
            try
            {
                await using (var input = await catalog.OpenPayloadAsync(record.PayloadLocator!, cancellationToken).ConfigureAwait(false))
                await using (var staged = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.Read,
                    BufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    payloadSha256 = await CopyAndHashAsync(
                        input,
                        staged,
                        record.PayloadByteLength,
                        cancellationToken).ConfigureAwait(false);
                    await staged.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                // Cache lookup is an optimization. Let the configured
                // resolver produce the authoritative duration when staging
                // cannot be done.
                return await _inner.ResolveAsync(catalog, record, cancellationToken).ConfigureAwait(false);
            }

            var key = new VoiceDurationCacheKey(record.SourceStableKey!, payloadSha256, DecoderVersion);
            var cached = await TryGetCachedAsync(key, cancellationToken).ConfigureAwait(false);
            if (cached is > 0)
            {
                return cached;
            }

            var resolved = _inner is IVoiceStreamDurationResolver streamResolver
                ? await ResolveStagedAsync(streamResolver, temporaryPath, cancellationToken).ConfigureAwait(false)
                : await _inner.ResolveAsync(catalog, record, cancellationToken).ConfigureAwait(false);
            if (resolved is > 0)
            {
                await TryStoreAsync(new VoiceDurationCacheEntry(key, resolved.Value, DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
            }
            return resolved;
        }
        finally
        {
            EnqueueCleanupIfNeeded(temporaryPath);
        }
    }

    private async Task<long?> ResolveWithKnownHashAsync(
        IVoiceCatalog catalog,
        VoiceRecord record,
        string payloadSha256,
        CancellationToken cancellationToken)
    {
        var key = new VoiceDurationCacheKey(record.SourceStableKey!, payloadSha256, DecoderVersion);
        var cached = await TryGetCachedAsync(key, cancellationToken).ConfigureAwait(false);

        if (cached is > 0)
        {
            return cached;
        }

        var resolved = await _inner.ResolveAsync(catalog, record, cancellationToken).ConfigureAwait(false);
        if (resolved is > 0)
        {
            await TryStoreAsync(new VoiceDurationCacheEntry(key, resolved.Value, DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
        }

        return resolved;
    }

    private async Task<long?> TryGetCachedAsync(
        VoiceDurationCacheKey key,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _cache.TryGetAsync(key, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task TryStoreAsync(
        VoiceDurationCacheEntry entry,
        CancellationToken cancellationToken)
    {
        try
        {
            await _cache.StoreAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // A read-only or temporarily unavailable cache must not turn a
            // valid duration calculation into a failed scan.
        }
    }

    private static async Task<long?> ResolveStagedAsync(
        IVoiceStreamDurationResolver resolver,
        string path,
        CancellationToken cancellationToken)
    {
        await using var staged = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await resolver.ResolveAsync(staged, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> CopyAndHashAsync(
        Stream input,
        Stream staged,
        long? expectedLength,
        CancellationToken cancellationToken)
    {
        if (!input.CanRead)
        {
            throw new InvalidDataException("The voice catalog returned a non-readable payload stream.");
        }

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

                hash.AppendData(buffer, 0, read);
                await staged.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                length = checked(length + read);
            }

            if (expectedLength is not null && length != expectedLength.Value)
            {
                throw new InvalidDataException("The payload length changed while preparing duration input.");
            }

            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private void EnqueueCleanupIfNeeded(string path)
    {
        var failure = TryDelete(path);
        if (failure is null || _cleanupQueue is null)
        {
            return;
        }

        try
        {
            _cleanupQueue.Enqueue(path, failure);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Cleanup diagnostics must not replace the duration result.
        }
    }

    private static CleanupDiagnostic? TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            File.Delete(path);
            return File.Exists(path)
                ? new CleanupDiagnostic("duration-input", "delete-still-present", nameof(IOException))
                : null;
        }
        catch (IOException exception)
        {
            return new CleanupDiagnostic("duration-input", "delete-failed", exception.GetType().Name);
        }
        catch (UnauthorizedAccessException exception)
        {
            return new CleanupDiagnostic("duration-input", "delete-failed", exception.GetType().Name);
        }
        catch (Exception exception)
        {
            return new CleanupDiagnostic("duration-input", "delete-failed", exception.GetType().Name);
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
