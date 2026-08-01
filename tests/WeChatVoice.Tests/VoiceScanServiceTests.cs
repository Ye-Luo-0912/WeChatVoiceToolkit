using System.Runtime.CompilerServices;
using WeChatVoice.Application;
using WeChatVoice.Core.Models;
using WeChatVoice.Core.Ports;

namespace WeChatVoice.Tests;

public sealed class VoiceScanServiceTests
{
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
}
