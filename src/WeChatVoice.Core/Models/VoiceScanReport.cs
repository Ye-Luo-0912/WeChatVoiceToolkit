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
        int SuspectedDuplicateCount,
        int InvalidHeaderCount = 0,
        int AmbiguousPayloadCount = 0,
        IReadOnlyDictionary<string, int>? PayloadStateCounts = null,
        bool DeepScan = false,
        int? ExportableVoiceCount = null,
        long TotalPayloadBytes = 0,
        int DurationKnownCount = 0)
    {
        this.MatchedVoiceCount = MatchedVoiceCount;
        this.TotalDurationMs = TotalDurationMs;
        this.EarliestOccurredAtUtc = EarliestOccurredAtUtc?.ToUniversalTime();
        this.LatestOccurredAtUtc = LatestOccurredAtUtc?.ToUniversalTime();
        this.ShardCounts = new ReadOnlyDictionary<string, int>(new Dictionary<string, int>(ShardCounts, StringComparer.OrdinalIgnoreCase));
        this.UnassociatedMediaCount = UnassociatedMediaCount;
        this.EmptyBlobCount = EmptyBlobCount;
        this.SuspectedDuplicateCount = SuspectedDuplicateCount;
        this.InvalidHeaderCount = InvalidHeaderCount;
        this.AmbiguousPayloadCount = AmbiguousPayloadCount;
        this.PayloadStateCounts = new ReadOnlyDictionary<string, int>(new Dictionary<string, int>(PayloadStateCounts ?? new Dictionary<string, int>(), StringComparer.OrdinalIgnoreCase));
        this.DeepScan = DeepScan;
        this.ExportableVoiceCount = ExportableVoiceCount ?? (PayloadStateCounts?.TryGetValue(nameof(VoicePayloadState.Linked), out var linked) == true ? linked : 0);
        this.TotalPayloadBytes = TotalPayloadBytes;
        this.DurationKnownCount = DurationKnownCount;
    }

    public int MatchedVoiceCount { get; }
    public long TotalDurationMs { get; }
    public DateTimeOffset? EarliestOccurredAtUtc { get; }
    public DateTimeOffset? LatestOccurredAtUtc { get; }
    public IReadOnlyDictionary<string, int> ShardCounts { get; }
    public int UnassociatedMediaCount { get; }
    public int EmptyBlobCount { get; }
    public int SuspectedDuplicateCount { get; }
    public int InvalidHeaderCount { get; }
    public int AmbiguousPayloadCount { get; }
    public IReadOnlyDictionary<string, int> PayloadStateCounts { get; }
    public bool DeepScan { get; }
    public int ExportableVoiceCount { get; }
    public int RejectedVoiceCount => Math.Max(0, MatchedVoiceCount - ExportableVoiceCount);
    public long TotalPayloadBytes { get; }
    public int DurationKnownCount { get; }
    public int DurationUnknownCount => Math.Max(0, MatchedVoiceCount - DurationKnownCount);
}
