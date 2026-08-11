namespace WeChatVoice.Core.Models;

/// <summary>
/// Ownership classification of an application-owned storage object. The
/// classification drives which objects may be cleaned automatically and which
/// are user assets that must never be deleted by GC.
/// </summary>
public enum StorageAssetKind
{
    /// <summary>Not an app-owned object; never touched by cleanup.</summary>
    Unknown,

    /// <summary>Reconstructable staging / temporary data eligible for automatic cleanup.</summary>
    Transient,

    /// <summary>A recoverable materialization that may be adopted, but is safe to
    /// reclaim after a retention window.</summary>
    RecoverableIntermediate,

    /// <summary>A verified Snapshot or completed Workspace that is reusable; only
    /// reclaimable through an explicit, policy-driven action.</summary>
    ReusableIntermediate,

    /// <summary>Raw SILK Export output owned by the user. Never auto-deleted.</summary>
    UserAsset,

    /// <summary>A curated Dataset derived from exports. Never auto-deleted.</summary>
    DerivedUserAsset,
}

/// <summary>
/// One inspected application-owned storage object. The record is immutable
/// after inspection; it carries only non-sensitive identity and size metadata.
/// </summary>
public sealed record StorageAssetRecord(
    StorageAssetKind Kind,
    string Path,
    long TotalBytes,
    string? WorkspaceId,
    string? SnapshotId,
    DateTimeOffset LastModifiedUtc,
    bool HasActiveLock,
    string? Note);

/// <summary>
/// Non-sensitive, per-category size summary produced by the storage inventory.
/// Hosts render these totals; they never infer ownership from the <see cref="RecentWorkspaceStore"/>.
/// </summary>
public sealed record StorageInventorySummary(
    long SnapshotBytes,
    long WorkspaceBytes,
    long ExportBytes,
    long DatasetBytes,
    long TempBytes,
    long RecoverableBytes,
    long SafelyReclaimableBytes,
    IReadOnlyList<StorageAssetRecord> Assets);

/// <summary>
/// Read-only preview of what a cleanup would reclaim. A preview never deletes;
/// the delete path repeats inspection and re-checks locks before removing an
/// object.
/// </summary>
public sealed record StorageCleanupPreview(
    int ItemCount,
    long TotalBytes,
    IReadOnlyList<StorageAssetRecord> Items);

/// <summary>
/// Outcome of a completed cleanup run. Only independent, app-owned transient
/// and expired-recoverable objects are removed; a skipped item carries a
/// non-sensitive reason.
/// </summary>
public sealed record StorageCleanupResult(
    int DeletedCount,
    long DeletedBytes,
    IReadOnlyList<string> SkippedReasons);

/// <summary>
/// A group of snapshots that share the same content fingerprint
/// (<see cref="SnapshotManifest.SnapshotId"/>). Retaining more than one copy is
/// redundant; the UI can surface this group so the user can keep only the newest
/// or most convenient copy.
/// </summary>
public sealed record DuplicateSnapshotGroup(
    string SnapshotId,
    IReadOnlyList<StorageAssetRecord> Copies);

/// <summary>
/// Disposition of one export run under the run/metadata retention policy.
/// </summary>
public enum RunRetentionDisposition
{
    /// <summary>Within the keep-recent window; journal and transaction are retained.</summary>
    KeepRecent,

    /// <summary>Bound to a dataset selection profile or build; never compacted.</summary>
    Referenced,

    /// <summary>Older than the keep window and unreferenced; safe to compact.</summary>
    Compactable,
}

/// <summary>
/// One export run inspected by <see cref="IRunRetentionWorkflow"/>. Carries only
/// non-sensitive identity and size metadata. <c>JournalBytes</c> and
/// <c>TransactionBytes</c> are the derived metadata that compaction would
/// remove; the committed manifests and artifact index are always retained.
/// </summary>
public sealed record RunRetentionItem(
    string RunId,
    RunRetentionDisposition Disposition,
    bool IsComplete,
    DateTimeOffset CreatedUtc,
    long JournalBytes,
    long TransactionBytes,
    long TotalBytes,
    string? Reason);

/// <summary>
/// Read-only preview of what run compaction would reclaim. A preview never
/// deletes; the compact path repeats inspection and re-checks references first.
/// </summary>
public sealed record RunRetentionPreview(
    int KeepRecent,
    int CompactableCount,
    long CompactableBytes,
    IReadOnlyList<RunRetentionItem> Items);

/// <summary>
/// Options for run/metadata retention. <c>KeepRecent</c> is the number of most
/// recent complete runs to retain in full; older unreferenced runs are
/// compacted (their journal and transaction removed, manifests retained).
/// </summary>
public sealed record RunRetentionOptions(string ExportRoot, int KeepRecent)
{
    public RunRetentionOptions(string ExportRoot)
        : this(ExportRoot, DefaultKeepRecent)
    {
    }

    public const int DefaultKeepRecent = 5;
}

/// <summary>
/// Outcome of a completed run compaction. Only independent, unreferenced,
/// out-of-window journals and transactions are removed; committed manifests and
/// the artifact index are never removed. A skipped run carries a reason.
/// </summary>
public sealed record RunRetentionResult(
    int CompactedCount,
    long CompactedBytes,
    IReadOnlyList<string> SkippedReasons);