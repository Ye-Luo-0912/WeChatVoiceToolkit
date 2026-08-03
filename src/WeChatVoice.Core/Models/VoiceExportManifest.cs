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
        IReadOnlyList<VoiceExportEntry>? Entries = null,
        IReadOnlyList<VoiceExportFailure>? Failures = null,
        string? RunId = null,
        string? SnapshotId = null,
        string? AdapterId = null,
        string? AccountId = null,
        string? DatasetId = null,
        string? AdapterVersion = null,
        IReadOnlyList<string>? DatabaseFingerprints = null,
        ExportRunStatus RunStatus = ExportRunStatus.Completed,
        bool Cancelled = false,
        MaterializationProvenance? Provenance = null,
        AccountIdentity? AccountIdentity = null,
        string? DatasetNamespaceKey = null)
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
        this.AccountIdentity = AccountIdentity ?? Core.Models.AccountIdentity.CandidateOnly;
        this.DatasetNamespaceKey = DatasetNamespaceKey;
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

    /// <summary>
    /// Technical account evidence and the independent user-confirmation state
    /// captured by the catalog that produced this run.
    /// </summary>
    public AccountIdentity AccountIdentity { get; }

    /// <summary>
    /// Private HMAC namespace used to derive portable item IDs. It is never
    /// emitted by <see cref="VoiceDatasetManifest"/> or the portable CSV.
    /// </summary>
    public string? DatasetNamespaceKey { get; init; }

    /// <summary>Duration of every successfully selected voice in this run.</summary>
    public long TotalDurationMs => SumDuration(Entries, trainingOnly: false);

    /// <summary>Duration represented by explicitly eligible and selected items.</summary>
    public long TotalTrainingDurationMs => SumDuration(Entries, trainingOnly: true);

    public long TotalPayloadBytes => SumBytes(Entries);

    public int TrainingEntryCount => Entries.Count(static entry => entry.IsTrainingSelected);

    private static long SumDuration(IEnumerable<VoiceExportEntry> entries, bool trainingOnly)
    {
        long total = 0;
        foreach (var entry in entries)
        {
            if ((!trainingOnly || entry.IsTrainingSelected) && entry.DurationMs is > 0)
            {
                total = checked(total + entry.DurationMs.Value);
            }
        }

        return total;
    }

    private static long SumBytes(IEnumerable<VoiceExportEntry> entries)
    {
        long total = 0;
        foreach (var entry in entries)
        {
            total = checked(total + Math.Max(0, entry.OriginalByteLength));
        }

        return total;
    }

    private static IReadOnlyList<T> Freeze<T>(IEnumerable<T>? values)
        => new ReadOnlyCollection<T>((values ?? Array.Empty<T>()).ToArray());
}

/// <summary>
/// Portable dataset metadata. This type deliberately has no account,
/// conversation, source database, stable source key, or local path fields.
/// </summary>
public sealed record VoiceDatasetManifest
{
    public VoiceDatasetManifest(
        DateTimeOffset GeneratedAtUtc,
        string RunId,
        IReadOnlyList<VoiceDatasetEntry>? Items = null,
        string Format = "wechatvoice-dataset-manifest-v1")
    {
        this.GeneratedAtUtc = GeneratedAtUtc.ToUniversalTime();
        this.RunId = RunId;
        this.Items = (Items ?? Array.Empty<VoiceDatasetEntry>()).ToArray();
        this.Format = Format;
    }

    public DateTimeOffset GeneratedAtUtc { get; }
    public string RunId { get; }
    public IReadOnlyList<VoiceDatasetEntry> Items { get; }
    public string Format { get; }

    public long TotalDurationMs => Items.Where(static item => item.DurationMs is > 0).Sum(static item => item.DurationMs!.Value);
    public long TotalByteLength => Items.Sum(static item => Math.Max(0, item.ByteLength));
}

public sealed record VoiceDatasetEntry
{
    public VoiceDatasetEntry(
        string ItemId,
        string RelativeAudioPath,
        string Sha256,
        long ByteLength,
        long? DurationMs,
        IReadOnlyList<string>? QualityFlags,
        TrainingEligibility TrainingEligibility,
        bool Selected)
    {
        this.ItemId = ItemId;
        this.RelativeAudioPath = RelativeAudioPath;
        this.Sha256 = Sha256;
        this.ByteLength = ByteLength;
        this.DurationMs = DurationMs;
        this.QualityFlags = (QualityFlags ?? Array.Empty<string>()).ToArray();
        this.TrainingEligibility = TrainingEligibility;
        this.Selected = Selected;
    }

    public string ItemId { get; }
    public string RelativeAudioPath { get; }
    public string Sha256 { get; }
    public long ByteLength { get; }
    public long? DurationMs { get; }
    public IReadOnlyList<string> QualityFlags { get; }
    public TrainingEligibility TrainingEligibility { get; }
    public bool Selected { get; }
}

public enum ExportRunStatus
{
    Completed,
    CompletedWithFailures,
    Cancelled,
    Failed,
}

public enum ExportState
{
    Exported,
    VerifiedExisting,
    Failed,
}

public enum TrainingEligibility
{
    Unknown,
    Eligible,
    Rejected,
}

public enum UserSelectionState
{
    NotSelected,
    Selected,
}

public sealed record VoiceExportRunContext(
    string RunId,
    VoiceCatalogContext CatalogContext,
    DateTimeOffset StartedAtUtc,
    string? SelectionFingerprint = null)
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
    string? ManifestSha256 = null,
    DateTimeOffset? ManifestGeneratedAtUtc = null)
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
    bool SelectedForTraining = false,
    ExportState ExportState = ExportState.Exported,
    TrainingEligibility TrainingEligibility = TrainingEligibility.Unknown,
    UserSelectionState UserSelectionState = UserSelectionState.NotSelected)
{
    public DateTimeOffset OccurredAtUtc { get; init; } = OccurredAtUtc.ToUniversalTime();

    public IReadOnlyList<string> QualityFlags { get; init; } = QualityFlags ?? Array.Empty<string>();

    /// <summary>
    /// Compatibility projection for consumers that used the old boolean.
    /// New exports leave that field false and use the three explicit states.
    /// </summary>
    public bool IsTrainingSelected
        => TrainingEligibility == Core.Models.TrainingEligibility.Eligible
            && UserSelectionState == Core.Models.UserSelectionState.Selected;
}

/// <summary>
/// Error information retained for a message or a run-level stage such as querying.
/// </summary>
public sealed record VoiceExportFailure(
    string? MessageId,
    string Stage,
    string Error,
    string? ExceptionType = null);
