namespace WeChatVoice.Core.Errors;

/// <summary>
/// Stable, machine-readable failure codes shared by the CLI, the Key Broker,
/// and future UI hosts. These codes are the UI contract: presentation layers
/// map a code to localized text; the lower layers never emit user-facing
/// sentences. Transport-level protocol errors (malformed_request, ...) are
/// intentionally not part of this catalog.
/// </summary>
public enum ErrorCode
{
    /// <summary>No verified Weixin process is running in the current session.</summary>
    WeixinNotRunning,

    /// <summary>The running Weixin build is not the version the Profile supports.</summary>
    UnsupportedWeixinVersion,

    /// <summary>A running Weixin process exists but its identity evidence does not match the selected Profile.</summary>
    ProcessIdentityMismatch,

    /// <summary>The snapshot manifest could not be verified against its directory.</summary>
    SnapshotInvalid,

    /// <summary>The snapshot or a database group changed between verification steps.</summary>
    SnapshotInconsistent,

    /// <summary>No candidate key validated every required database group.</summary>
    KeyCandidateNotFound,

    /// <summary>The verified key acquisition did not cover every required source database.</summary>
    DatabaseGroupUncovered,

    /// <summary>The SQLCipher worker bundle failed signature or hash trust verification.</summary>
    WorkerBundleUntrusted,

    /// <summary>The SQLCipher worker process failed or rejected the database.</summary>
    WorkerFailed,

    /// <summary>The materialization output failed an independent validation step.</summary>
    MaterializationInvalid,

    /// <summary>The produced or loaded local workspace is not valid.</summary>
    WorkspaceInvalid,

    /// <summary>No verified adapter supports the discovered database schema.</summary>
    UnsupportedSchema,

    /// <summary>The requested stable contact could not be resolved.</summary>
    ContactNotFound,

    /// <summary>An export run completed with per-message failures.</summary>
    ExportPartialFailure,

    /// <summary>Account identity is only a candidate; explicit user confirmation is required.</summary>
    AccountConfirmationRequired,
}
