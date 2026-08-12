namespace WeChatVoice.Core.Models;

/// <summary>
/// Classification of a project stage during a resume/inspection pass. The
/// classification is derived only from re-verified local state (workspace
/// JSON, materialization commit marker, and output hashes), never from the
/// mere existence of files.
/// </summary>
public enum ProjectStageState
{
    /// <summary>No verifiable local state exists yet; the stage must be created.</summary>
    Missing,

    /// <summary>Local state exists, re-verifies, and can be reused without re-running the expensive chain.</summary>
    ValidReusable,

    /// <summary>Local state exists but is incomplete; it can be recovered/adopted without recompute.</summary>
    Recoverable,

    /// <summary>Local state exists but is bound to a source that no longer matches (old data).</summary>
    Stale,

    /// <summary>Local state exists but cannot be verified or safely recovered; it must be recomputed.</summary>
    Invalid,

    /// <summary>Another operation currently holds the state; do not touch it.</summary>
    Busy,
}

/// <summary>
/// A single project stage's inspection result. The UI shows this to let the
/// user choose "continue existing project" vs "refresh from source" without
/// re-running the expensive chain.
/// </summary>
public sealed record ProjectStageStatus(
    ProjectStageState State,
    string? WorkspacePath,
    string? MaterializedRoot,
    string? AccountId,
    string? Reason,
    bool RequiresElevation,
    bool ProducesNewDiskData,
    VerifiedLocalWorkspace? VerifiedWorkspace = null);
