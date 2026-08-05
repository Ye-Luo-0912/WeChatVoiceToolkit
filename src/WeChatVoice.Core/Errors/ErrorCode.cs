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

    /// <summary>The selected contact's voice rows include a group-chat speaker, which the first release refuses to export.</summary>
    GroupChatNotSupported,

    /// <summary>An export run completed with per-message failures.</summary>
    ExportPartialFailure,

    /// <summary>Account identity is only a candidate; explicit user confirmation is required.</summary>
    AccountConfirmationRequired,

    /// <summary>The elevated Key Broker could not start because the user declined the UAC prompt.</summary>
    UacElevationRejected,

    /// <summary>A workflow boundary returned an unexpected failure.</summary>
    WorkflowFailed,

    /// <summary>The host supplied an incomplete or invalid workflow request.</summary>
    InvalidRequest,

    /// <summary>A different high-cost Desktop operation is currently active.</summary>
    OperationBusy,

    /// <summary>The voice set changed between the immutable scan and export.</summary>
    SelectionPlanMismatch,

    /// <summary>The caller requested duration analysis but no resolver is configured.</summary>
    DurationResolverUnavailable,

    /// <summary>Automatic Weixin data-source discovery failed.</summary>
    DataSourceDiscoveryFailed,

    /// <summary>Automatic data-source discovery hit its time or directory budget.</summary>
    DataSourceDiscoveryTruncated,

    /// <summary>No selectable Weixin data source was found.</summary>
    NoDataSourceFound,

    /// <summary>More than one selectable Weixin account requires explicit choice.</summary>
    MultipleAccountsRequireSelection,

    /// <summary>The selected data source failed layout, identity, or file validation.</summary>
    SelectedDataSourceInvalid,

    /// <summary>A stable snapshot cannot start while Weixin is running.</summary>
    WeixinStillRunning,

    /// <summary>The snapshot output path failed safety validation.</summary>
    SnapshotOutputInvalid,

    /// <summary>The snapshot destination does not have enough available space.</summary>
    InsufficientDiskSpace,
}
