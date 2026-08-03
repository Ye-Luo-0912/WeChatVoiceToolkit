using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using WeChatVoice.Application;
using WeChatVoice.Core.Models;
using WeChatVoice.Core.Ports;
using WeChatVoice.Infrastructure.Audio;
using WeChatVoice.Workflows.Composition;

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
    public async Task Cached_resolver_hashes_and_stages_the_payload_in_one_catalog_read()
    {
        using var temporary = new TestTemporaryDirectory();
        await using var cache = new JsonlVoiceDurationCache(
            temporary.GetPath("duration-cache.jsonl"),
            "stream-decoder-v1");
        var decoder = new StreamingDurationResolver();
        var catalog = new FakeCatalog();
        var payload = "#!SILK_V3"u8.ToArray();
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
            PayloadByteLength: payload.Length);

        var duration = await new CachedVoiceDurationResolver(decoder, cache).ResolveAsync(
            catalog,
            record,
            CancellationToken.None);

        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)).ToLowerInvariant();
        Assert.Equal(1234, duration);
        Assert.Equal(1, catalog.OpenPayloadCount);
        Assert.Equal(1, decoder.StreamCalls);
        Assert.Equal(payload.Length, decoder.BytesRead);
        Assert.Equal(1234, await cache.TryGetAsync(
            new VoiceDurationCacheKey(record.SourceStableKey!, hash, "stream-decoder-v1"),
            CancellationToken.None));
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

    [Fact]
    public async Task Composition_root_disposes_the_configured_duration_resolver()
    {
        var resolver = new DisposableDurationResolver();

        await using (var root = new WorkflowCompositionRoot(
            new TestAccountConfirmation(),
            voiceDurationResolver: resolver))
        {
            Assert.True(root.DurationAnalysisAvailable);
        }

        Assert.Equal(1, resolver.DisposeCount);
    }

    [Fact]
    public async Task Cleanup_queue_retries_private_temporary_paths_without_exposing_them_in_diagnostics()
    {
        using var temporary = new TestTemporaryDirectory();
        var path = temporary.GetPath("private-input.silk");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        var queue = new TemporaryFileCleanupQueue();
        queue.Enqueue(path, new CleanupDiagnostic("duration-input", "delete-failed", nameof(IOException)));

        await queue.RetryPendingAsync(CancellationToken.None);

        Assert.False(File.Exists(path));
        Assert.Empty(queue.GetSnapshot());
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
        public int OpenPayloadCount { get; private set; }

        public VoiceCatalogContext Context { get; } = new("dataset", "adapter", "1", "account", ["fingerprint"]);
        public async IAsyncEnumerable<ContactRecord> QueryContactsAsync(ContactQuery query, [EnumeratorCancellation] CancellationToken cancellationToken) { await Task.CompletedTask; yield break; }
        public async IAsyncEnumerable<VoiceRecord> QueryVoicesAsync(VoiceQuery query, [EnumeratorCancellation] CancellationToken cancellationToken) { await Task.CompletedTask; yield break; }
        public ValueTask<Stream> OpenPayloadAsync(VoicePayloadLocator locator, CancellationToken cancellationToken)
        {
            OpenPayloadCount++;
            return ValueTask.FromResult<Stream>(new MemoryStream("#!SILK_V3"u8.ToArray()));
        }
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

    private sealed class StreamingDurationResolver : IVersionedVoiceDurationResolver, IVoiceStreamDurationResolver
    {
        public int StreamCalls { get; private set; }
        public int BytesRead { get; private set; }
        public string DecoderVersion => "stream-decoder-v1";

        public Task<long?> ResolveAsync(IVoiceCatalog catalog, VoiceRecord record, CancellationToken cancellationToken)
            => throw new InvalidOperationException("The catalog-based resolver path should not be used.");

        public async Task<long?> ResolveAsync(Stream payload, CancellationToken cancellationToken)
        {
            StreamCalls++;
            var buffer = new byte[32];
            while (true)
            {
                var read = await payload.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;
                BytesRead += read;
            }

            return 1234;
        }
    }

    private sealed class DisposableDurationResolver : IVersionedVoiceDurationResolver, IAsyncDisposable
    {
        public string DecoderVersion => "test-decoder-v1";

        public int DisposeCount { get; private set; }

        public Task<long?> ResolveAsync(IVoiceCatalog catalog, VoiceRecord record, CancellationToken cancellationToken)
            => Task.FromResult<long?>(null);

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestAccountConfirmation : IAccountConfirmation
    {
        public Task<AccountConfirmation> ConfirmAsync(AccountIdentityReport report, CancellationToken cancellationToken)
            => Task.FromResult(new AccountConfirmation(true, report.AccountCandidate));
    }
}
