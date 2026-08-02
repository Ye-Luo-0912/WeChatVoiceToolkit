using System.Text;
using System.Text.Json;
using WeChatVoice.Core.Models;
using WeChatVoice.Core.Ports;
using WeChatVoice.Infrastructure.Serialization;

namespace WeChatVoice.Infrastructure.Audio;

/// <summary>
/// Append-only deep-scan cache. The verified database group fingerprint is
/// part of every key, so a cache entry never becomes evidence for a changed
/// Workspace. A malformed or torn line is ignored as a non-authoritative
/// optimization record.
/// </summary>
public sealed class JsonlVoicePayloadHashCache : IVoicePayloadHashCache
{
    private const int MaxLineLength = 64 * 1024;
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<VoicePayloadHashCacheKey, string>? _entries;
    private long _loadedLength = -1;
    private long _loadedLastWriteTicks = -1;
    private int _disposed;

    public JsonlVoicePayloadHashCache(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
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
            return _entries!.TryGetValue(key, out var hash) ? hash : null;
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
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            if (_entries!.TryGetValue(entry.Key, out var existing)
                && string.Equals(existing, entry.PayloadSha256, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var directory = Path.GetDirectoryName(_path)
                ?? throw new InvalidDataException("The deep-scan cache path has no parent directory.");
            Directory.CreateDirectory(directory);
            var line = JsonSerializer.Serialize(
                new CacheLine(
                    entry.Key.SourceStableKey,
                    entry.Key.PayloadByteLength,
                    entry.Key.DatabaseFingerprint,
                    entry.PayloadSha256,
                    entry.UpdatedAtUtc),
                InfrastructureJson.Compact) + Environment.NewLine;
            await File.AppendAllTextAsync(_path, line, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            _entries[entry.Key] = entry.PayloadSha256;
            UpdateLoadedStamp();
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
            _entries = new Dictionary<VoicePayloadHashCacheKey, string>();
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

        var entries = new Dictionary<VoicePayloadHashCacheKey, string>();
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
            if (line.Length == 0 || line.Length > MaxLineLength)
            {
                continue;
            }

            CacheLine? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<CacheLine>(line, InfrastructureJson.Compact);
            }
            catch (JsonException)
            {
                continue;
            }

            if (parsed is null
                || string.IsNullOrWhiteSpace(parsed.SourceStableKey)
                || parsed.PayloadByteLength <= 0
                || string.IsNullOrWhiteSpace(parsed.DatabaseFingerprint)
                || !IsSha256(parsed.PayloadSha256))
            {
                continue;
            }

            var key = new VoicePayloadHashCacheKey(
                parsed.SourceStableKey,
                parsed.PayloadByteLength,
                parsed.DatabaseFingerprint);
            entries[key] = parsed.PayloadSha256.ToLowerInvariant();
        }

        _entries = entries;
        _loadedLength = info.Length;
        _loadedLastWriteTicks = info.LastWriteTimeUtc.Ticks;
    }

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

    private sealed record CacheLine(
        string SourceStableKey,
        long PayloadByteLength,
        string DatabaseFingerprint,
        string PayloadSha256,
        DateTimeOffset UpdatedAtUtc);
}
