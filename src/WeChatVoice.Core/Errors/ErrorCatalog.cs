namespace WeChatVoice.Core.Errors;

/// <summary>
/// Single authoritative mapping from <see cref="ErrorCode"/> to the stable,
/// non-sensitive error payload. Every host (Broker, CLI, future UI) reads
/// the same catalog so a code always means the same thing.
/// </summary>
public static class ErrorCatalog
{
    public static AppError Get(ErrorCode code) => code switch
    {
        ErrorCode.WeixinNotRunning => new AppError(code, IsRetryable: true, "start-weixin", "weixin-process-not-found"),
        ErrorCode.UnsupportedWeixinVersion => new AppError(code, IsRetryable: false, "use-supported-version", "unsupported-weixin-version"),
        ErrorCode.ProcessIdentityMismatch => new AppError(code, IsRetryable: false, "restart-weixin-and-retry", "weixin-identity-mismatch"),
        ErrorCode.SnapshotInvalid => new AppError(code, IsRetryable: false, "re-snapshot", "snapshot-verification-failed"),
        ErrorCode.SnapshotInconsistent => new AppError(code, IsRetryable: true, "re-snapshot", "snapshot-content-changed"),
        ErrorCode.KeyCandidateNotFound => new AppError(code, IsRetryable: true, "restart-weixin-and-retry", "no-candidate-key-validated"),
        ErrorCode.DatabaseGroupUncovered => new AppError(code, IsRetryable: false, "re-snapshot", "database-group-uncovered"),
        ErrorCode.WorkerBundleUntrusted => new AppError(code, IsRetryable: false, "reinstall-package", "worker-bundle-untrusted"),
        ErrorCode.WorkerFailed => new AppError(code, IsRetryable: true, "retry-materialization", "worker-failed"),
        ErrorCode.MaterializationInvalid => new AppError(code, IsRetryable: false, "retry-materialization", "materialization-validation-failed"),
        ErrorCode.WorkspaceInvalid => new AppError(code, IsRetryable: false, "re-materialize", "workspace-invalid"),
        ErrorCode.UnsupportedSchema => new AppError(code, IsRetryable: false, "use-supported-schema", "unsupported-schema"),
        ErrorCode.ContactNotFound => new AppError(code, IsRetryable: false, "choose-contact", "contact-not-found"),
        ErrorCode.ExportPartialFailure => new AppError(code, IsRetryable: true, "review-failures", "export-partial-failure"),
        ErrorCode.AccountConfirmationRequired => new AppError(code, IsRetryable: true, "confirm-account", "account-confirmation-required"),
        ErrorCode.UacElevationRejected => new AppError(code, IsRetryable: true, "retry-materialization", "uac-elevation-rejected"),
        ErrorCode.WorkflowFailed => new AppError(code, IsRetryable: true, "retry", "workflow-failed"),
        ErrorCode.InvalidRequest => new AppError(code, IsRetryable: false, "complete-request", "invalid-request"),
        _ => new AppError(code, IsRetryable: false, "retry", "unknown-error"),
    };
}
