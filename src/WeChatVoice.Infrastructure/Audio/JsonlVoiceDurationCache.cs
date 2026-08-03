using System.Text;
using System.Text.Json;
using WeChatVoice.Core.Models;
using WeChatVoice.Core.Ports;
using WeChatVoice.Infrastructure.Serialization;

namespace WeChatVoice.Infrastructure.Audio;

/// <summary>
/// Durable duration cache with a bounded, compacted JSONL representation.
/// The source key is hashed on disk; the actual payload hash and decoder
/// identity remain part of the lookup key. Cache data is an optimization only.
/// </summary>
public sealed class JsonlVoiceDurationCache : IVoiceDurationCache
{
    private const int MaxLineLength = 64 * 1024;
    private readonly string _path;
    private readonly JsonlCacheFileStore _fileStore;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<DurationLookupKey, DurationCacheValue>? _entries;
    private int _lineCount;
    private bool _requiresCompaction;
    private long _loadedLength = -1;
    private long _loadedLastWriteTicks = -1;
    private int _disposed;

    public JsonlVoiceDurationCache(string path, string decoderVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(decoderVersion);
        _path = Path.GetFullPath(path);
        _fileStore = new JsonlCacheFileStore(_path);
        DecoderVersion = decoderVersion;
    }

    public string DecoderVersion { get; }

    public async ValueTask<long?> TryGetAsync(
        VoiceDurationCacheKey key,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        EnsureNotDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(key.DecoderVersion, DecoderVersion, StringComparison.Ordinal))
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureNotDisposed();
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            if (_requiresCompaction)
            {
                await using var writeLock = await _fileStore.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
                await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
                if (_requiresCompaction)
                {
                    await CompactAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            return _entries!.TryGetValue(ToLookupKey(key), out var value) ? value.DurationMs : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask StoreAsync(
        VoiceDurationCacheEntry entry,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        EnsureNotDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(entry.Key.DecoderVersion, DecoderVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The duration cache entry uses a different decoder version.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureNotDisposed();
            await using var writeLock = await _fileStore.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            var lookupKey = ToLookupKey(entry.Key);
            if (_entries!.TryGetValue(lookupKey, out var existing) && existing.DurationMs == entry.DurationMs)
            {
                if (_requiresCompaction)
                {
                    await CompactAsync(cancellationToken).ConfigureAwait(false);
                }

                return;
            }

            var line = JsonSerializer.Serialize(
                new DurationCacheLine(
                    lookupKey.SourceStableKeyHash,
                    null,
                    entry.Key.PayloadSha256,
                    entry.Key.DecoderVersion,
                    entry.DurationMs,
                    entry.UpdatedAtUtc),
                InfrastructureJson.Compact);
            await _fileStore.AppendLineAsync(line, cancellationToken).ConfigureAwait(false);
            _entries[lookupKey] = new DurationCacheValue(entry.DurationMs, entry.UpdatedAtUtc);
            _lineCount++;
            UpdateLoadedStamp();
            if (JsonlCacheFileStore.ShouldCompact(new FileInfo(_path).Length, _lineCount, _entries.Count, _requiresCompaction))
            {
                await CompactAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _gate.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        var info = new FileInfo(_path);
        if (!info.Exists)
        {
            _entries = new Dictionary<DurationLookupKey, DurationCacheValue>();
            _lineCount = 0;
            _requiresCompaction = false;
            _loadedLength = 0;
            _loadedLastWriteTicks = 0;
            return;
        }

        if (_entries is not null
            && _loadedLength == info.Length
            && _loadedLastWriteTicks == info.LastWriteTimeUtc.Ticks)
        {
            return;
        }

        var entries = new Dictionary<DurationLookupKey, DurationCacheValue>();
        var lineCount = 0;
        var requiresCompaction = false;
        var expiration = DateTimeOffset.UtcNow - JsonlCacheFileStore.EntryRetention;
        await using var stream = new FileStream(
            _path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, 64 * 1024);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            lineCount++;
            if (line.Length == 0 || line.Length > MaxLineLength)
            {
                requiresCompaction = true;
                continue;
            }

            DurationCacheLine? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<DurationCacheLine>(line, InfrastructureJson.Compact);
            }
            catch (JsonException)
            {
                requiresCompaction = true;
                continue;
            }

            if (parsed is null
                || !string.Equals(parsed.DecoderVersion, DecoderVersion, StringComparison.Ordinal)
                || parsed.DurationMs <= 0
                || !IsSha256(parsed.PayloadSha256))
            {
                requiresCompaction = true;
                continue;
            }

            if (parsed.UpdatedAtUtc < expiration)
            {
                requiresCompaction = true;
                continue;
            }

            var sourceHash = parsed.SourceStableKeyHash;
            if (!IsSha256(sourceHash))
            {
                sourceHash = string.IsNullOrWhiteSpace(parsed.SourceStableKey)
                    ? null
                    : JsonlCacheFileStore.HashSourceStableKey(parsed.SourceStableKey);
                requiresCompaction = true;
            }

            if (sourceHash is null)
            {
                requiresCompaction = true;
                continue;
            }

            entries[new DurationLookupKey(sourceHash, parsed.PayloadSha256.ToLowerInvariant(), parsed.DecoderVersion)] =
                new DurationCacheValue(parsed.DurationMs, parsed.UpdatedAtUtc);
        }

        if (entries.Count > JsonlCacheFileStore.MaximumEntries)
        {
            entries = entries
                .OrderByDescending(static pair => pair.Value.UpdatedAtUtc)
                .Take(JsonlCacheFileStore.MaximumEntries)
                .ToDictionary(static pair => pair.Key, static pair => pair.Value);
            requiresCompaction = true;
        }

        _entries = entries;
        _lineCount = lineCount;
        _requiresCompaction = requiresCompaction;
        _loadedLength = info.Length;
        _loadedLastWriteTicks = info.LastWriteTimeUtc.Ticks;
    }

    private async Task CompactAsync(CancellationToken cancellationToken)
    {
        var entries = _entries!;
        if (entries.Count > JsonlCacheFileStore.MaximumEntries)
        {
            entries = entries
                .OrderByDescending(static pair => pair.Value.UpdatedAtUtc)
                .ThenBy(static pair => pair.Key.SourceStableKeyHash, StringComparer.Ordinal)
                .ThenBy(static pair => pair.Key.PayloadSha256, StringComparer.Ordinal)
                .ThenBy(static pair => pair.Key.DecoderVersion, StringComparer.Ordinal)
                .Take(JsonlCacheFileStore.MaximumEntries)
                .ToDictionary(static pair => pair.Key, static pair => pair.Value);
            _entries = entries;
        }

        var lines = entries.Select(static pair => JsonSerializer.Serialize(
            new DurationCacheLine(
                pair.Key.SourceStableKeyHash,
                null,
                pair.Key.PayloadSha256,
                pair.Key.DecoderVersion,
                pair.Value.DurationMs,
                pair.Value.UpdatedAtUtc),
            InfrastructureJson.Compact));
        await _fileStore.ReplaceAsync(lines, cancellationToken).ConfigureAwait(false);
        _lineCount = entries.Count;
        _requiresCompaction = false;
        UpdateLoadedStamp();
    }

    private static DurationLookupKey ToLookupKey(VoiceDurationCacheKey key)
        => new(JsonlCacheFileStore.HashSourceStableKey(key.SourceStableKey), key.PayloadSha256.ToLowerInvariant(), key.DecoderVersion);

    private void UpdateLoadedStamp()
    {
        var info = new FileInfo(_path);
        _loadedLength = info.Exists ? info.Length : 0;
        _loadedLastWriteTicks = info.Exists ? info.LastWriteTimeUtc.Ticks : 0;
    }

    private void EnsureNotDisposed()
        => ObjectDisposedException.ThrowIf(_disposed != 0, this);

    private static bool IsSha256(string? value)
        => value is not null && value.Length == 64 && value.All(Uri.IsHexDigit);

    private readonly record struct DurationLookupKey(
        string SourceStableKeyHash,
        string PayloadSha256,
        string DecoderVersion);

    private readonly record struct DurationCacheValue(
        long DurationMs,
        DateTimeOffset UpdatedAtUtc);

    private sealed record DurationCacheLine(
        string? SourceStableKeyHash,
        string? SourceStableKey,
        string PayloadSha256,
        string DecoderVersion,
        long DurationMs,
        DateTimeOffset UpdatedAtUtc);
}
