using System.Collections.ObjectModel;

namespace WeChatVoice.Core.Models;

/// <summary>
/// A portable record of one export run. Per-message failures do not prevent a
/// manifest from being produced for the successfully exported entries.
/// </summary>
public sealed record VoiceExportManifest
{
    public VoiceExportManifest(
        DateTimeOffset GeneratedAtUtc,
        IEnumerable<VoiceExportEntry>? Entries = null,
        IEnumerable<VoiceExportFailure>? Failures = null,
        string? RunId = null,
        string? SnapshotId = null,
        string? AdapterId = null,
        string? AccountId = null)
    {
        this.GeneratedAtUtc = GeneratedAtUtc.ToUniversalTime();
        this.Entries = Freeze(Entries);
        this.Failures = Freeze(Failures);
        this.RunId = string.IsNullOrWhiteSpace(RunId) ? Guid.NewGuid().ToString("N") : RunId;
        this.SnapshotId = SnapshotId;
        this.AdapterId = AdapterId;
        this.AccountId = AccountId;
    }

    public DateTimeOffset GeneratedAtUtc { get; }

    public IReadOnlyList<VoiceExportEntry> Entries { get; }

    public IReadOnlyList<VoiceExportFailure> Failures { get; }

    public string RunId { get; }

    public string? SnapshotId { get; }

    public string? AdapterId { get; }

    public string? AccountId { get; }

    private static IReadOnlyList<T> Freeze<T>(IEnumerable<T>? values)
        => new ReadOnlyCollection<T>((values ?? Array.Empty<T>()).ToArray());
}

/// <summary>
/// A successfully persisted original voice payload, with an optional decoded WAV.
/// Paths are relative to the export root and use forward slashes.
/// </summary>
public sealed record VoiceExportEntry(
    string MessageId,
    string ConversationId,
    DateTimeOffset OccurredAtUtc,
    VoiceDirection Direction,
    string OriginalPath,
    long OriginalByteLength,
    string OriginalSha256,
    string? DecodedPath,
    string? ExportKey = null,
    bool WasSkipped = false,
    string? SourceDatabase = null,
    string? ShardId = null,
    long? DurationMs = null,
    string? SilkSha256 = null,
    string? WavSha256 = null,
    string? SpeakerId = null,
    bool HasDecodeError = false,
    IReadOnlyList<string>? QualityFlags = null,
    bool SelectedForTraining = false)
{
    public DateTimeOffset OccurredAtUtc { get; init; } = OccurredAtUtc.ToUniversalTime();

    public IReadOnlyList<string> QualityFlags { get; init; } = QualityFlags ?? Array.Empty<string>();
}

/// <summary>
/// Error information retained for a message or a run-level stage such as querying.
/// </summary>
public sealed record VoiceExportFailure(
    string? MessageId,
    string Stage,
    string Error,
    string? ExceptionType = null);
