using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using WeChatVoice.Application;
using WeChatVoice.Core.Models;
using WeChatVoice.Core.Ports;
using WeChatVoice.Infrastructure.Audio;

namespace WeChatVoice.Tests;

public sealed class DecoderVoiceDurationResolverTests
{
    [Fact]
    public async Task Jsonl_cache_round_trips_entries_and_isolates_decoder_versions()
    {
        using var temporary = new TestTemporaryDirectory();
        var path = temporary.GetPath(".wechatvoice", "duration-cache.jsonl");
        var key = new VoiceDurationCacheKey("adapter|account|contact|message|media", new string('a', 64), "decoder-v1");
        await using (var cache = new JsonlVoiceDurationCache(path, "decoder-v1"))
        {
            await cache.StoreAsync(new VoiceDurationCacheEntry(key, 1234, DateTimeOffset.UtcNow), CancellationToken.None);
            Assert.Equal(1234, await cache.TryGetAsync(key, CancellationToken.None));
            Assert.Null(await cache.TryGetAsync(new VoiceDurationCacheKey(key.SourceStableKey, key.PayloadSha256, "decoder-v2"), CancellationToken.None));
        }

        var lines = await File.ReadAllLinesAsync(path);
        Assert.Single(lines);
        Assert.Contains("durationMs", lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Jsonl_duration_cache_compacts_duplicate_and_expired_private_keys()
    {
        using var temporary = new TestTemporaryDirectory();
        var path = temporary.GetPath(".wechatvoice", "duration-cache.jsonl");
        var sourceKey = "adapter|account|private-contact|message|media";
        var payloadHash = new string('c', 64);
        var key = new VoiceDurationCacheKey(sourceKey, payloadHash, "decoder-v1");

        // Simulate a pre-hash cache line from an older build. It must be
        // dropped during the first read and must not remain as readable PII.
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(
            path,
            "{\"sourceStableKey\":\"" + sourceKey + "\",\"payloadSha256\":\"" + payloadHash
            + "\",\"decoderVersion\":\"decoder-v1\",\"durationMs\":123,\"updatedAtUtc\":\"2000-01-01T00:00:00Z\"}\n");

        await using var cache = new JsonlVoiceDurationCache(path, "decoder-v1");
        Assert.Null(await cache.TryGetAsync(key, CancellationToken.None));
        Assert.DoesNotContain(sourceKey, await File.ReadAllTextAsync(path), StringComparison.Ordinal);

        for (var index = 1; index <= 320; index++)
        {
            await cache.StoreAsync(
                new VoiceDurationCacheEntry(key, index, DateTimeOffset.UtcNow),
                CancellationToken.None);
        }

        var lines = await File.ReadAllLinesAsync(path);
        Assert.True(lines.Length < 100, $"duplicate cache lines were not compacted: {lines.Length}");
        Assert.DoesNotContain(sourceKey, await File.ReadAllTextAsync(path), StringComparison.Ordinal);
        Assert.Equal(320, await cache.TryGetAsync(key, CancellationToken.None));
    }

    [Fact]
    public async Task Cached_resolver_does_not_start_decoder_when_content_key_is_cached()
    {
        using var temporary = new TestTemporaryDirectory();
        await using var cache = new JsonlVoiceDurationCache(
            temporary.GetPath("duration-cache.jsonl"),
            "fake-decoder-v1");
        var payloadHash = new string('b', 64);
        var record = new VoiceRecord(
            "message",
            "contact",
            DateTimeOffset.UtcNow,
            VoiceDirection.Incoming,
            new VoicePayloadLocator("media", 0, "1"),
            AdapterId: "adapter",
            AccountId: "account",
            DataSetId: "dataset",
            AdapterVersion: "1",
            PayloadSha256: payloadHash,
            PayloadByteLength: 9);
        await cache.StoreAsync(
            new VoiceDurationCacheEntry(new VoiceDurationCacheKey(record.SourceStableKey!, payloadHash, "fake-decoder-v1"), 987, DateTimeOffset.UtcNow),
            CancellationToken.None);
        var decoder = new CountingDurationResolver();

        var result = await new CachedVoiceDurationResolver(decoder, cache).ResolveAsync(
            new FakeCatalog(),
            record,
            CancellationToken.None);

        Assert.Equal(987, result);
        Assert.Equal(0, decoder.Calls);
    }
    [Fact]
    public async Task Resolver_reads_duration_from_valid_pcm_wav_without_persisting_output()
    {
        var catalog = new FakeCatalog();
        await using var resolver = new DecoderVoiceDurationResolver(new FixedWavDecoder(48000));
        var record = new VoiceRecord("m1", "peer", DateTimeOffset.UtcNow, VoiceDirection.Incoming,
            new VoicePayloadLocator("media", 0, "1"), PayloadState: VoicePayloadState.Linked);

        var duration = await resolver.ResolveAsync(catalog, record, CancellationToken.None);

        Assert.Equal(1000, duration);
    }

    private sealed class FixedWavDecoder(int dataBytes) : IVoiceDecoder
    {
        public async Task DecodeAsync(Stream input, Stream output, CancellationToken cancellationToken)
        {
            var wav = new byte[44 + dataBytes];
            "RIFF"u8.CopyTo(wav);
            BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(4), (uint)(wav.Length - 8));
            "WAVE"u8.CopyTo(wav.AsSpan(8));
            "fmt "u8.CopyTo(wav.AsSpan(12));
            BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(16), 16);
            BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(20), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(22), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(24), 24000);
            BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(28), 48000);
            BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(32), 2);
            BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(34), 16);
            "data"u8.CopyTo(wav.AsSpan(36));
            BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(40), (uint)dataBytes);
            await output.WriteAsync(wav, cancellationToken);
        }
    }

    private sealed class FakeCatalog : IVoiceCatalog
    {
        public VoiceCatalogContext Context { get; } = new("dataset", "adapter", "1", "account", ["fingerprint"]);
        public async IAsyncEnumerable<ContactRecord> QueryContactsAsync(ContactQuery query, [EnumeratorCancellation] CancellationToken cancellationToken) { await Task.CompletedTask; yield break; }
        public async IAsyncEnumerable<VoiceRecord> QueryVoicesAsync(VoiceQuery query, [EnumeratorCancellation] CancellationToken cancellationToken) { await Task.CompletedTask; yield break; }
        public ValueTask<Stream> OpenPayloadAsync(VoicePayloadLocator locator, CancellationToken cancellationToken)
            => ValueTask.FromResult<Stream>(new MemoryStream("#!SILK_V3"u8.ToArray()));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CountingDurationResolver : IVersionedVoiceDurationResolver
    {
        public int Calls { get; private set; }

        public string DecoderVersion => "fake-decoder-v1";

        public Task<long?> ResolveAsync(IVoiceCatalog catalog, VoiceRecord record, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<long?>(1);
        }
    }
}
