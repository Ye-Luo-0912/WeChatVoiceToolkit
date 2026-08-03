using System.Text;
using System.Text.Json;
using WeChatVoice.Core.Models;
using WeChatVoice.Core.Ports;
using WeChatVoice.Infrastructure.Serialization;

namespace WeChatVoice.Infrastructure.Audio;

/// <summary>
/// Durable deep-scan cache with hashed source identities, cross-process
/// single-writer coordination, and bounded atomic compaction.
/// </summary>
public sealed class JsonlVoicePayloadHashCache : IVoicePayloadHashCache
{
    private const int MaxLineLength = 64 * 1024;
    private readonly string _path;
    private readonly JsonlCacheFileStore _fileStore;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<PayloadLookupKey, PayloadCacheValue>? _entries;
    private int _lineCount;
    private bool _requiresCompaction;
    private long _loadedLength = -1;
    private long _loadedLastWriteTicks = -1;
    private int _disposed;

    public JsonlVoicePayloadHashCache(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _fileStore = new JsonlCacheFileStore(_path);
    }

    public async ValueTask<string?> TryGetAsync(
        VoicePayloadHashCacheKey key,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        EnsureNotDisposed();
        cancellationToken.ThrowIfCancellationRequested();
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

            return _entries!.TryGetValue(ToLookupKey(key), out var value) ? value.PayloadSha256 : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask StoreAsync(
        VoicePayloadHashCacheEntry entry,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        EnsureNotDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureNotDisposed();
            await using var writeLock = await _fileStore.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            var lookupKey = ToLookupKey(entry.Key);
            if (_entries!.TryGetValue(lookupKey, out var existing)
                && string.Equals(existing.PayloadSha256, entry.PayloadSha256, StringComparison.OrdinalIgnoreCase))
            {
                if (_requiresCompaction)
                {
                    await CompactAsync(cancellationToken).ConfigureAwait(false);
                }

                return;
            }

            var line = JsonSerializer.Serialize(
                new CacheLine(
                    lookupKey.SourceStableKeyHash,
                    null,
                    entry.Key.PayloadByteLength,
                    entry.Key.DatabaseFingerprint,
                    entry.PayloadSha256,
                    entry.UpdatedAtUtc),
                InfrastructureJson.Compact);
            await _fileStore.AppendLineAsync(line, cancellationToken).ConfigureAwait(false);
            _entries[lookupKey] = new PayloadCacheValue(entry.PayloadSha256, entry.UpdatedAtUtc);
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
            _entries = new Dictionary<PayloadLookupKey, PayloadCacheValue>();
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

        var entries = new Dictionary<PayloadLookupKey, PayloadCacheValue>();
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

            CacheLine? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<CacheLine>(line, InfrastructureJson.Compact);
            }
            catch (JsonException)
            {
                requiresCompaction = true;
                continue;
            }

            if (parsed is null
                || parsed.PayloadByteLength <= 0
                || string.IsNullOrWhiteSpace(parsed.DatabaseFingerprint)
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

            var key = new PayloadLookupKey(sourceHash, parsed.PayloadByteLength, parsed.DatabaseFingerprint);
            entries[key] = new PayloadCacheValue(parsed.PayloadSha256.ToLowerInvariant(), parsed.UpdatedAtUtc);
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
                .ThenBy(static pair => pair.Key.PayloadByteLength)
                .ThenBy(static pair => pair.Key.DatabaseFingerprint, StringComparer.Ordinal)
                .Take(JsonlCacheFileStore.MaximumEntries)
                .ToDictionary(static pair => pair.Key, static pair => pair.Value);
            _entries = entries;
        }

        var lines = entries.Select(static pair => JsonSerializer.Serialize(
            new CacheLine(
                pair.Key.SourceStableKeyHash,
                null,
                pair.Key.PayloadByteLength,
                pair.Key.DatabaseFingerprint,
                pair.Value.PayloadSha256,
                pair.Value.UpdatedAtUtc),
            InfrastructureJson.Compact));
        await _fileStore.ReplaceAsync(lines, cancellationToken).ConfigureAwait(false);
        _lineCount = entries.Count;
        _requiresCompaction = false;
        UpdateLoadedStamp();
    }

    private static PayloadLookupKey ToLookupKey(VoicePayloadHashCacheKey key)
        => new(JsonlCacheFileStore.HashSourceStableKey(key.SourceStableKey), key.PayloadByteLength, key.DatabaseFingerprint);

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

    private readonly record struct PayloadLookupKey(
        string SourceStableKeyHash,
        long PayloadByteLength,
        string DatabaseFingerprint);

    private readonly record struct PayloadCacheValue(
        string PayloadSha256,
        DateTimeOffset UpdatedAtUtc);

    private sealed record CacheLine(
        string? SourceStableKeyHash,
        string? SourceStableKey,
        long PayloadByteLength,
        string DatabaseFingerprint,
        string PayloadSha256,
        DateTimeOffset UpdatedAtUtc);
}
