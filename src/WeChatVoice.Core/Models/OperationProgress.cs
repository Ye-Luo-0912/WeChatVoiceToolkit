using WeChatVoice.Core.Errors;

namespace WeChatVoice.Core.Models;

/// <summary>
/// Which shared workflow produced a progress event. Hosts (CLI, Desktop) use
/// this to route progress to the right panel without string matching.
/// </summary>
public enum OperationPhase
{
    EnvironmentAssessment,
    Snapshot,
    Materialization,
    Workspace,
    ContactDiscovery,
    VoiceScan,
    VoiceExport,
    ProjectState,
    StorageLifecycle,
}

/// <summary>Current status of a workflow run as seen by a host.</summary>
public enum OperationStatus
{
    Running,
    WaitingForUser,
    Completed,
    Cancelled,
    Failed,
}

/// <summary>
/// Well-known progress stage identifiers emitted by the shared workflows.
/// Stages are typed constants so hosts can present them without parsing
/// arbitrary strings; each workflow may add its own stable constants.
/// </summary>
public static class OperationStageIds
{
    public const string Starting = "starting";
    public const string Preparing = "preparing";
    public const string DetectingWeixin = "detecting-weixin";
    public const string VerifyingSnapshot = "verifying-snapshot";
    public const string CopyingFiles = "copying-files";
    public const string ConfirmingAccount = "confirming-account";
    public const string AcquiringKey = "acquiring-key";
    public const string Materializing = "materializing";
    public const string VerifyingWorkspace = "verifying-workspace";
    public const string LoadingWorkspace = "loading-workspace";
    public const string ResolvingContact = "resolving-contact";
    public const string QueryingVoices = "querying-voices";
    public const string Exporting = "exporting";
    public const string Committing = "committing";
    public const string InspectingState = "inspecting-state";
    public const string ResumingState = "resuming-state";

    // Storage lifecycle
    public const string ScanningStorage = "scanning-storage";
    public const string PreviewingCleanup = "previewing-cleanup";
    public const string CleaningStorage = "cleaning-storage";

    public const string Completing = "completing";
}

/// <summary>
/// One typed progress update. <see cref="Id"/> is a well-known stage constant,
/// <see cref="Message"/> stays non-sensitive, and <see cref="PercentComplete"/>
/// is optional (0..100) for host progress bars.
/// </summary>
public sealed record OperationStage(string Id, string? Message = null, double? PercentComplete = null);

public sealed record OperationProgress(
    OperationPhase Phase,
    OperationStatus Status,
    OperationStage Stage);

/// <summary>
/// Unified typed operation failure payload. Hosts map <see cref="Code"/> to
/// localized text through <see cref="WeChatVoice.Core.Errors.ErrorCatalog"/>
/// and use <see cref="IsRetryable"/> to enable retry affordances.
/// </summary>
public sealed record OperationError(
    ErrorCode Code,
    bool IsRetryable,
    string? SuggestedAction,
    string? NonSensitiveTechnicalContext)
{
    public static OperationError From(ErrorCode code)
    {
        var error = Errors.ErrorCatalog.Get(code);
        return new OperationError(code, error.IsRetryable, error.SuggestedAction, error.NonSensitiveTechnicalContext);
    }
}
