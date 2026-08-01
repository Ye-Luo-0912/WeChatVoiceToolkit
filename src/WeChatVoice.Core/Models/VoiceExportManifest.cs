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
        string? AccountId = null,
        string? DatasetId = null,
        string? AdapterVersion = null,
        IReadOnlyList<string>? DatabaseFingerprints = null,
        ExportRunStatus RunStatus = ExportRunStatus.Completed,
        bool Cancelled = false,
        MaterializationProvenance? Provenance = null)
    {
        this.GeneratedAtUtc = GeneratedAtUtc.ToUniversalTime();
        this.Entries = Freeze(Entries);
        this.Failures = Freeze(Failures);
        this.RunId = string.IsNullOrWhiteSpace(RunId) ? Guid.NewGuid().ToString("N") : RunId;
        this.SnapshotId = SnapshotId;
        this.AdapterId = AdapterId;
        this.AccountId = AccountId;
        this.DatasetId = DatasetId;
        this.AdapterVersion = AdapterVersion;
        this.DatabaseFingerprints = Freeze(DatabaseFingerprints);
        this.RunStatus = RunStatus;
        this.Cancelled = Cancelled || RunStatus == ExportRunStatus.Cancelled;
        this.Provenance = Provenance;
    }

    public DateTimeOffset GeneratedAtUtc { get; }

    public IReadOnlyList<VoiceExportEntry> Entries { get; }

    public IReadOnlyList<VoiceExportFailure> Failures { get; }

    public string RunId { get; }

    public string? SnapshotId { get; }

    public string? AdapterId { get; }

    public string? AccountId { get; }

    public string? DatasetId { get; }

    public string? AdapterVersion { get; }

    public IReadOnlyList<string> DatabaseFingerprints { get; }

    public ExportRunStatus RunStatus { get; }

    public bool Cancelled { get; }

    /// <summary>
    /// Full workspace materialization provenance (key-extraction Profile,
    /// Weixin version, module hashes, backend bundle). The final voice data
    /// manifest inherits the entire workspace provenance so a later consumer
    /// can audit exactly which verified source produced the voices.
    /// </summary>
    public MaterializationProvenance? Provenance { get; }

    private static IReadOnlyList<T> Freeze<T>(IEnumerable<T>? values)
        => new ReadOnlyCollection<T>((values ?? Array.Empty<T>()).ToArray());
}

public enum ExportRunStatus
{
    Completed,
    CompletedWithFailures,
    Cancelled,
    Failed,
}

public sealed record VoiceExportRunContext(
    string RunId,
    VoiceCatalogContext CatalogContext,
    DateTimeOffset StartedAtUtc)
{
    public DateTimeOffset StartedAtUtc { get; init; } = StartedAtUtc.ToUniversalTime();
}

public sealed record VoiceExportJournalEvent(
    string Event,
    string RunId,
    DateTimeOffset OccurredAtUtc,
    string? MessageId = null,
    VoiceExportEntry? Entry = null,
    VoiceExportFailure? Failure = null,
    VoiceCatalogContext? Context = null,
    bool Cancelled = false,
    string? ManifestSha256 = null)
{
    public DateTimeOffset OccurredAtUtc { get; init; } = OccurredAtUtc.ToUniversalTime();
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
    string? SourceStableKey = null,
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
