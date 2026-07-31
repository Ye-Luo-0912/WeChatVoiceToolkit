using WeChatVoice.Core.Models;

namespace WeChatVoice.Tests;

public sealed class CoreModelTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void VoiceMessage_requires_a_nonblank_message_identifier(string messageId)
    {
        var exception = Assert.Throws<ArgumentException>(() => new VoiceMessage(
            messageId,
            "conversation",
            DateTimeOffset.UtcNow,
            VoiceDirection.Incoming));

        Assert.Equal("MessageId", exception.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void VoiceMessage_requires_a_nonblank_conversation_identifier(string conversationId)
    {
        var exception = Assert.Throws<ArgumentException>(() => new VoiceMessage(
            "message",
            conversationId,
            DateTimeOffset.UtcNow,
            VoiceDirection.Incoming));

        Assert.Equal("ConversationId", exception.ParamName);
    }

    [Fact]
    public void VoiceMessage_rejects_an_empty_payload_reference_and_normalizes_its_timestamp()
    {
        var emptyReference = Assert.Throws<ArgumentException>(() => new VoiceMessage(
            "message",
            "conversation",
            DateTimeOffset.UtcNow,
            VoiceDirection.Incoming,
            string.Empty));
        Assert.Equal("PayloadReference", emptyReference.ParamName);

        var sourceTime = new DateTimeOffset(2026, 7, 31, 9, 30, 0, TimeSpan.FromHours(8));
        var message = new VoiceMessage(
            "message",
            "conversation",
            sourceTime,
            VoiceDirection.Outgoing,
            "source-token");

        Assert.Equal(sourceTime.UtcDateTime, message.OccurredAtUtc.UtcDateTime);
        Assert.Equal(TimeSpan.Zero, message.OccurredAtUtc.Offset);
        Assert.Equal("source-token", message.PayloadReference);
    }

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
    public void VoiceExportPaths_requires_absolute_media_paths_and_safe_relative_manifest_paths()
    {
        using var temporary = new TestTemporaryDirectory();
        var originalPath = temporary.GetPath("media", "input.silk");
        var decodedPath = temporary.GetPath("media", "input.wav");

        var paths = new VoiceExportPaths(
            originalPath,
            decodedPath,
            "original\\2026\\07\\input.silk",
            "decoded//2026/07/input.wav");

        Assert.Equal(Path.GetFullPath(originalPath), paths.OriginalFilePath);
        Assert.Equal(Path.GetFullPath(decodedPath), paths.DecodedFilePath);
        Assert.Equal("original/2026/07/input.silk", paths.OriginalManifestPath);
        Assert.Equal("decoded/2026/07/input.wav", paths.DecodedManifestPath);

        Assert.Equal("OriginalFilePath", Assert.Throws<ArgumentException>(() => new VoiceExportPaths(
            "relative.silk", decodedPath, "original/file.silk", "decoded/file.wav")).ParamName);
        Assert.Equal("DecodedFilePath", Assert.Throws<ArgumentException>(() => new VoiceExportPaths(
            originalPath, temporary.GetPath("media", "input.silk"), "original/file.silk", "decoded/file.wav")).ParamName);
        Assert.Equal("OriginalManifestPath", Assert.Throws<ArgumentException>(() => new VoiceExportPaths(
            originalPath, decodedPath, "../outside.silk", "decoded/file.wav")).ParamName);
        Assert.Equal("DecodedManifestPath", Assert.Throws<ArgumentException>(() => new VoiceExportPaths(
            originalPath, decodedPath, "original/file.silk", Path.GetFullPath(temporary.GetPath("outside.wav")))).ParamName);
    }
}
