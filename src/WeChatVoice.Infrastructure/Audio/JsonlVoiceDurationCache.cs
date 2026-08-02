using System.Text;
using System.Text.Json;
using WeChatVoice.Core.Models;
using WeChatVoice.Core.Ports;
using WeChatVoice.Infrastructure.Serialization;

namespace WeChatVoice.Infrastructure.Audio;

/// <summary>
/// Append-only local duration cache. The in-memory index makes repeated scans
/// independent of the number of historical JSONL lines; the file remains
/// human-auditable and can be discarded without affecting the source or export
/// boundary.
/// </summary>
public sealed class JsonlVoiceDurationCache : IVoiceDurationCache
{
    private const int MaxLineLength = 64 * 1024;
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<VoiceDurationCacheKey, long>? _entries;
    private long _loadedLength = -1;
    private long _loadedLastWriteTicks = -1;
    private int _disposed;

    public JsonlVoiceDurationCache(string path, string decoderVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(decoderVersion);
        _path = Path.GetFullPath(path);
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
            return _entries!.TryGetValue(key, out var duration) ? duration : null;
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
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            if (_entries!.TryGetValue(entry.Key, out var existing) && existing == entry.DurationMs)
            {
                return;
            }

            var directory = Path.GetDirectoryName(_path)
                ?? throw new InvalidDataException("The duration cache path has no parent directory.");
            Directory.CreateDirectory(directory);
            var line = JsonSerializer.Serialize(
                new DurationCacheLine(
                    entry.Key.SourceStableKey,
                    entry.Key.PayloadSha256,
                    entry.Key.DecoderVersion,
                    entry.DurationMs,
                    entry.UpdatedAtUtc),
                InfrastructureJson.Compact) + Environment.NewLine;
            var bytes = Encoding.UTF8.GetBytes(line);
            await using (var stream = new FileStream(
                _path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            _entries[entry.Key] = entry.DurationMs;
            UpdateLoadedStamp();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _gate.Dispose();
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        var info = new FileInfo(_path);
        if (!info.Exists)
        {
            _entries = new Dictionary<VoiceDurationCacheKey, long>();
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

        var entries = new Dictionary<VoiceDurationCacheKey, long>();
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

            DurationCacheLine? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<DurationCacheLine>(line, InfrastructureJson.Compact);
            }
            catch (JsonException)
            {
                // A truncated final line is expected after a process crash;
                // malformed cache data is simply ignored as non-authoritative.
                continue;
            }

            if (parsed is null
                || !string.Equals(parsed.DecoderVersion, DecoderVersion, StringComparison.Ordinal)
                || parsed.DurationMs <= 0
                || !IsSha256(parsed.PayloadSha256)
                || string.IsNullOrWhiteSpace(parsed.SourceStableKey))
            {
                continue;
            }

            var key = new VoiceDurationCacheKey(
                parsed.SourceStableKey,
                parsed.PayloadSha256.ToLowerInvariant(),
                parsed.DecoderVersion);
            entries[key] = parsed.DurationMs;
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

    private static bool IsSha256(string value)
        => value.Length == 64 && value.All(Uri.IsHexDigit);

    private sealed record DurationCacheLine(
        string SourceStableKey,
        string PayloadSha256,
        string DecoderVersion,
        long DurationMs,
        DateTimeOffset UpdatedAtUtc);
}
