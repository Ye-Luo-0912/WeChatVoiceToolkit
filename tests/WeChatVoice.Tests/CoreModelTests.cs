using WeChatVoice.Core.Models;

namespace WeChatVoice.Tests;

public sealed class CoreModelTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void VoiceQuery_rejects_nonpositive_maximum_results(int maximumResults)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => new VoiceQuery(MaximumResults: maximumResults));

        Assert.Equal("MaximumResults", exception.ParamName);
    }

    [Fact]
    public void VoiceQuery_normalizes_optional_filters_and_rejects_an_inverted_utc_time_range()
    {
        var from = new DateTimeOffset(2026, 7, 31, 9, 0, 0, TimeSpan.FromHours(8));
        var to = new DateTimeOffset(2026, 7, 31, 3, 0, 0, TimeSpan.Zero);
        var query = new VoiceQuery("  ", VoiceDirection.Incoming, from, to, MaximumResults: 5);

        Assert.Null(query.ConversationId);
        Assert.Equal(VoiceDirection.Incoming, query.Direction);
        Assert.Equal(TimeSpan.Zero, query.FromUtc!.Value.Offset);
        Assert.Equal(TimeSpan.Zero, query.ToUtc!.Value.Offset);
        Assert.Equal(5, query.MaximumResults);

        var inverted = Assert.Throws<ArgumentException>(() => new VoiceQuery(
            FromUtc: to.AddMinutes(1),
            ToUtc: from));
        Assert.Equal("FromUtc", inverted.ParamName);
    }

    [Fact]
    public void VoiceRecord_builds_a_stable_export_key_from_dataset_identity()
    {
        var record = new VoiceRecord(
            "message",
            "conversation",
            DateTimeOffset.UtcNow,
            VoiceDirection.Incoming,
            new VoicePayloadLocator("media", 2, "blob"),
            SnapshotId: "snapshot",
            AdapterId: "adapter",
            AccountId: "account",
            ShardId: "2",
            SourceMessageKey: "source-key");

        Assert.Equal("snapshot|adapter|account|2|conversation|source-key", record.StableExportKey);
    }

    [Fact]
    public void RawSnapshot_can_override_a_manifest_path_when_a_snapshot_is_moved()
    {
        using var temporary = new TestTemporaryDirectory();
        var recordedPath = temporary.CreateDirectory("recorded");
        var movedPath = temporary.CreateDirectory("moved");
        var manifest = new SnapshotManifest(recordedPath, recordedPath, DateTimeOffset.UtcNow);

        var snapshot = new RawSnapshot("snapshot", manifest, movedPath);

        Assert.Equal(Path.GetFullPath(movedPath), snapshot.SnapshotDirectory);
        Assert.Equal(Path.GetFullPath(movedPath), snapshot.SnapshotDirectoryOverride);
    }
}
