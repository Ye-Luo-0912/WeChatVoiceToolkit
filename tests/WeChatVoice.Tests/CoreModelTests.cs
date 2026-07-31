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

        Assert.Equal("adapter|account|conversation|source-key|media:2:blob", record.SourceStableKey);
        Assert.Equal("snapshot", record.Provenance.SnapshotId);
    }

    [Fact]
    public void RawSnapshot_can_override_a_manifest_path_when_a_snapshot_is_moved()
    {
        using var temporary = new TestTemporaryDirectory();
        var recordedPath = temporary.CreateDirectory("recorded");
        var movedPath = temporary.CreateDirectory("moved");
        var manifest = new SnapshotManifest(recordedPath, recordedPath, DateTimeOffset.UtcNow);

        var snapshot = new RawSnapshot(manifest, movedPath);

        Assert.Equal(Path.GetFullPath(movedPath), snapshot.SnapshotDirectory);
        Assert.Equal(Path.GetFullPath(movedPath), snapshot.SnapshotDirectoryOverride);
        Assert.Equal(manifest.SnapshotId, snapshot.SnapshotId);
    }

    [Fact]
    public void SnapshotManifest_id_is_content_addressed_and_cannot_be_overridden()
    {
        var files = new[]
        {
            new SnapshotFileRecord("b.db", 2, "bb", DateTimeOffset.UtcNow),
            new SnapshotFileRecord("a.db", 1, "aa", DateTimeOffset.UtcNow),
        };
        var first = new SnapshotManifest("C:\\source", "C:\\snapshot", DateTimeOffset.UtcNow, files);
        var reordered = new SnapshotManifest("D:\\other", "D:\\moved", DateTimeOffset.UtcNow, files.Reverse().ToArray());

        Assert.Equal(first.SnapshotId, reordered.SnapshotId);
        Assert.Throws<ArgumentException>(() => new SnapshotManifest(
            "C:\\source",
            "C:\\snapshot",
            DateTimeOffset.UtcNow,
            files,
            SnapshotId: new string('0', 64)));
    }

    [Fact]
    public void VoiceRecord_requires_locator_only_when_media_is_linked()
    {
        var exception = Assert.Throws<ArgumentException>(() => new VoiceRecord(
            "message",
            "conversation",
            DateTimeOffset.UtcNow,
            VoiceDirection.Incoming,
            null));
        Assert.Equal("PayloadLocator", exception.ParamName);

        var unassociated = new VoiceRecord(
            "message",
            "conversation",
            DateTimeOffset.UtcNow,
            VoiceDirection.Incoming,
            null,
            MediaLinked: false);
        Assert.Null(unassociated.PayloadLocator);
        Assert.Null(unassociated.SourceStableKey);
    }
}
