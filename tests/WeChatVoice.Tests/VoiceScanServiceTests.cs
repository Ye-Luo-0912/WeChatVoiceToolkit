using System.Runtime.CompilerServices;
using WeChatVoice.Application;
using WeChatVoice.Core.Models;
using WeChatVoice.Core.Ports;
using WeChatVoice.Infrastructure.Audio;

namespace WeChatVoice.Tests;

public sealed class VoiceScanServiceTests
{
    [Fact]
    public void Guided_export_eligibility_requires_complete_provenance_and_stable_source_identity()
    {
        var context = new VoiceCatalogContext(
            "dataset",
            "adapter",
            "v1",
            "account",
            ["db-fingerprint"],
            "snapshot");
        var query = new VoiceQuery(
            ConversationId: "peer",
            Direction: VoiceDirection.Incoming,
            ContactUsername: "peer",
            ContactId: "peer");
        var record = CreateGuidedRecord();

        var evaluator = new VoiceExportEligibilityEvaluator();
        var accepted = evaluator.Evaluate(record, context, query);
        Assert.True(accepted.IsEligible, accepted.Detail);

        var incomplete = CreateGuidedRecord(dataSetId: null);
        var rejected = evaluator.Evaluate(incomplete, context, query);
        Assert.False(rejected.IsEligible);
        Assert.Equal("provenance", rejected.ReasonCode);

        var sourceUnbound = CreateGuidedRecord(adapterId: null);
        rejected = evaluator.Evaluate(sourceUnbound, context, query);
        Assert.False(rejected.IsEligible);
        Assert.Equal("source-identity", rejected.ReasonCode);

        static VoiceRecord CreateGuidedRecord(
            string? dataSetId = "dataset",
            string? adapterId = "adapter")
            => new(
                "message",
                "peer",
                DateTimeOffset.UtcNow,
                VoiceDirection.Incoming,
                new VoicePayloadLocator("media", 0, "blob"),
                SourceDatabase: "media.db",
                PayloadByteLength: 10,
                SpeakerId: "peer",
                SnapshotId: "snapshot",
                AdapterId: adapterId,
                AccountId: "account",
                DataSetId: dataSetId,
                AdapterVersion: "v1",
                DatabaseFingerprints: ["db-fingerprint"],
                AdapterFamily: adapterId,
                AccountStableId: "account",
                ConversationStableId: "peer",
                PayloadState: VoicePayloadState.Linked);
    }

    [Fact]
    public async Task ScanAsync_reports_duration_shards_missing_media_empty_blobs_and_duplicates()
    {
        var records = new[]
        {
            new VoiceRecord("one", "conversation", DateTimeOffset.UtcNow.AddMinutes(-2), VoiceDirection.Incoming, new VoicePayloadLocator("media", 0, "one"), ShardId: "0", DurationMs: 1200, PayloadSha256: "same", PayloadByteLength: 4, AdapterId: "adapter", AccountId: "account"),
            new VoiceRecord("two", "conversation", DateTimeOffset.UtcNow.AddMinutes(-1), VoiceDirection.Incoming, null, ShardId: "1", DurationMs: 800, MediaLinked: false, AdapterId: "adapter", AccountId: "account"),
            new VoiceRecord("three", "conversation", DateTimeOffset.UtcNow, VoiceDirection.Incoming, null, ShardId: "1", PayloadByteLength: 0, MediaLinked: false, PayloadState: VoicePayloadState.Empty, AdapterId: "adapter", AccountId: "account"),
        };

        var report = await new VoiceScanService(new FakeCatalog(records)).ScanAsync(new VoiceQuery(Direction: VoiceDirection.Incoming));

        Assert.Equal(3, report.MatchedVoiceCount);
        Assert.Equal(2000, report.TotalDurationMs);
        Assert.Equal(1, report.UnassociatedMediaCount);
        Assert.Equal(1, report.EmptyBlobCount);
        Assert.Equal(0, report.SuspectedDuplicateCount);
        Assert.Equal(1, report.ShardCounts["0"]);
        Assert.Equal(2, report.ShardCounts["1"]);
    }

    [Fact]
    public async Task ScanAsync_resolves_missing_duration_only_when_requested()
    {
        var record = new VoiceRecord("decode-me", "conversation", DateTimeOffset.UtcNow, VoiceDirection.Incoming,
            new VoicePayloadLocator("media", 0, "1"), PayloadByteLength: 10, PayloadState: VoicePayloadState.Linked,
            AdapterId: "adapter", AccountId: "account");
        var catalog = new FakeCatalog([record]);
        var resolver = new FakeDurationResolver(1500);

        var withoutDecode = await new VoiceScanService(catalog, resolver).ScanAsync(new VoiceQuery(Direction: VoiceDirection.Incoming));
        Assert.Equal(0, withoutDecode.TotalDurationMs);
        Assert.Equal(0, withoutDecode.DurationKnownCount);

        var withDecode = await new VoiceScanService(catalog, resolver).ScanAsync(new VoiceQuery(Direction: VoiceDirection.Incoming, ResolveDuration: true));
        Assert.Equal(1500, withDecode.TotalDurationMs);
        Assert.Equal(1, withDecode.DurationKnownCount);
        Assert.Equal(0, withDecode.DurationUnknownCount);
    }

    [Fact]
    public async Task ScanAsync_applies_maximum_results_after_duration_and_payload_filters()
    {
        var records = Enumerable.Range(1, 5)
            .Select(index => new VoiceRecord(
                $"filtered-{index}",
                "conversation",
                DateTimeOffset.UtcNow.AddMinutes(index),
                VoiceDirection.Incoming,
                new VoicePayloadLocator("media", 0, index.ToString()),
                PayloadByteLength: index * 10,
                DurationMs: index * 1000,
                AdapterId: "adapter",
                AccountId: "account"))
            .ToArray();

        var report = await new VoiceScanService(new FakeCatalog(records), new RecordDurationResolver()).ScanAsync(
            new VoiceQuery(
                Direction: VoiceDirection.Incoming,
                MaximumResults: 2,
                ResolveDuration: true,
                MinimumDurationMs: 2000,
                MinimumPayloadBytes: 20));

        Assert.Equal(2, report.MatchedVoiceCount);
        Assert.Equal(2, report.ExportableVoiceCount);
        Assert.Equal(50, report.TotalPayloadBytes);
    }

    [Fact]
    public async Task Deep_scan_reuses_payload_hash_cache_without_reading_payload_again()
    {
        using var temporary = new TestTemporaryDirectory();
        var payload = new byte[] { 0x23, 0x42, 0x61, 0x7f };
        var record = new VoiceRecord(
            "cached-hash",
            "conversation",
            DateTimeOffset.UtcNow,
            VoiceDirection.Incoming,
            new VoicePayloadLocator("media", 0, "cached-hash"),
            PayloadByteLength: payload.Length,
            AdapterId: "adapter",
            AccountId: "account");
        var catalog = new CountingPayloadCatalog(record, payload);
        await using var cache = new JsonlVoicePayloadHashCache(temporary.GetPath(".wechatvoice", "deep-scan-cache.jsonl"));
        var query = new VoiceQuery(Direction: VoiceDirection.Incoming, DeepScan: true);

        var first = await new VoiceScanService(catalog, payloadHashCache: cache).ScanAsync(query);
        var firstOpenCount = catalog.OpenPayloadCount;
        var second = await new VoiceScanService(catalog, payloadHashCache: cache).ScanAsync(query);

        Assert.Equal(1, firstOpenCount);
        Assert.Equal(firstOpenCount, catalog.OpenPayloadCount);
        Assert.Equal(first.ResultSetFingerprint, second.ResultSetFingerprint);
        Assert.True(File.Exists(temporary.GetPath(".wechatvoice", "deep-scan-cache.jsonl")));
    }

    [Fact]
    public async Task Large_scan_uses_a_verified_disk_spool_and_releases_it_after_read()
    {
        var records = Enumerable.Range(0, PreparedSelectionSpool.InMemoryRecordLimit + 1)
            .Select(index => new VoiceRecord(
                $"spooled-{index}",
                "conversation",
                DateTimeOffset.UtcNow.AddSeconds(index),
                VoiceDirection.Incoming,
                new VoicePayloadLocator("media", 0, index.ToString()),
                PayloadByteLength: 10,
                AdapterId: "adapter",
                AccountId: "account"))
            .ToArray();
        var catalog = new FakeCatalog(records);

        var result = await new VoiceScanService(catalog).ScanWithRecordsAsync(
            new VoiceQuery(Direction: VoiceDirection.Incoming),
            CancellationToken.None);

        Assert.NotNull(result.Spool);
        var spool = result.Spool!;
        Assert.Empty(result.Records);
        Assert.Equal(records.Length, spool.RecordCount);
        Assert.Equal(records.Length, await CountAsync(PreparedSelectionSpool.ReadAsync(spool, CancellationToken.None)));
        Assert.True(File.Exists(spool.Path));

        await PreparedSelectionSpool.DeleteAsync(spool, cancellationToken: CancellationToken.None);
        Assert.False(File.Exists(spool.Path));
    }

    private static async Task<int> CountAsync(IAsyncEnumerable<VoiceRecord> source)
    {
        var count = 0;
        await foreach (var _ in source.ConfigureAwait(false))
        {
            count++;
        }

        return count;
    }

    private sealed class FakeDurationResolver(long duration) : IVoiceDurationResolver
    {
        public Task<long?> ResolveAsync(IVoiceCatalog catalog, VoiceRecord record, CancellationToken cancellationToken)
            => Task.FromResult<long?>(duration);
    }

    private sealed class RecordDurationResolver : IVoiceDurationResolver
    {
        public Task<long?> ResolveAsync(IVoiceCatalog catalog, VoiceRecord record, CancellationToken cancellationToken)
            => Task.FromResult(record.DurationMs);
    }

    private sealed class FakeCatalog(IReadOnlyList<VoiceRecord> records) : IVoiceCatalog
    {
        public VoiceCatalogContext Context { get; } = new("dataset", "adapter", "1", "account", ["db-fingerprint"]);

        public async IAsyncEnumerable<ContactRecord> QueryContactsAsync(ContactQuery query, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield return new ContactRecord("contact", query.Username, "nickname", "remark", ConversationId: "conversation");
        }

        public async IAsyncEnumerable<VoiceRecord> QueryVoicesAsync(VoiceQuery query, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var record in records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (query.Direction is null || query.Direction == record.Direction)
                {
                    yield return record;
                }

                await Task.Yield();
            }
        }

        public ValueTask<Stream> OpenPayloadAsync(VoicePayloadLocator locator, CancellationToken cancellationToken)
            => ValueTask.FromResult<Stream>(new MemoryStream());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CountingPayloadCatalog(VoiceRecord record, byte[] payload) : IVoiceCatalog
    {
        public VoiceCatalogContext Context { get; } = new("dataset", "adapter", "1", "account", ["db-fingerprint"]);

        public int OpenPayloadCount { get; private set; }

        public async IAsyncEnumerable<ContactRecord> QueryContactsAsync(ContactQuery query, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }

        public async IAsyncEnumerable<VoiceRecord> QueryVoicesAsync(VoiceQuery query, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (query.Direction is null || query.Direction == record.Direction)
            {
                yield return record;
            }

            await Task.CompletedTask;
        }

        public ValueTask<Stream> OpenPayloadAsync(VoicePayloadLocator locator, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OpenPayloadCount++;
            return ValueTask.FromResult<Stream>(new MemoryStream(payload, writable: false));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
