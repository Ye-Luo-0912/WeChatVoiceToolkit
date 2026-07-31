using System.Collections.ObjectModel;

namespace WeChatVoice.Core.Models;

public sealed record VoiceScanReport
{
    public VoiceScanReport(
        int MatchedVoiceCount,
        long TotalDurationMs,
        DateTimeOffset? EarliestOccurredAtUtc,
        DateTimeOffset? LatestOccurredAtUtc,
        IReadOnlyDictionary<string, int> ShardCounts,
        int UnassociatedMediaCount,
        int EmptyBlobCount,
        int SuspectedDuplicateCount)
    {
        this.MatchedVoiceCount = MatchedVoiceCount;
        this.TotalDurationMs = TotalDurationMs;
        this.EarliestOccurredAtUtc = EarliestOccurredAtUtc?.ToUniversalTime();
        this.LatestOccurredAtUtc = LatestOccurredAtUtc?.ToUniversalTime();
        this.ShardCounts = new ReadOnlyDictionary<string, int>(new Dictionary<string, int>(ShardCounts, StringComparer.OrdinalIgnoreCase));
        this.UnassociatedMediaCount = UnassociatedMediaCount;
        this.EmptyBlobCount = EmptyBlobCount;
        this.SuspectedDuplicateCount = SuspectedDuplicateCount;
    }

    public int MatchedVoiceCount { get; }
    public long TotalDurationMs { get; }
    public DateTimeOffset? EarliestOccurredAtUtc { get; }
    public DateTimeOffset? LatestOccurredAtUtc { get; }
    public IReadOnlyDictionary<string, int> ShardCounts { get; }
    public int UnassociatedMediaCount { get; }
    public int EmptyBlobCount { get; }
    public int SuspectedDuplicateCount { get; }
}
