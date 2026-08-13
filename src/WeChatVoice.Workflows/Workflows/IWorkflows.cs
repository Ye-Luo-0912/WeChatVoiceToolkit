using WeChatVoice.Core.Models;
using WeChatVoice.Windows;
using WeChatVoice.Workflows.Broker;
using KeyProfileMetadataModel = WeChatVoice.KeyProfileMetadata.KeyProfileMetadata;

namespace WeChatVoice.Workflows.Workflows;

/// <summary>
/// Ports implemented by the shared workflows. Hosts (CLI, Desktop, tests)
/// depend on these interfaces so a UI host never touches Infrastructure,
/// SQLite, or the Key Broker implementation directly, and tests can inject
/// fake backends.
/// </summary>

public interface IEnvironmentAssessmentWorkflow
{
    Task<EnvironmentAssessmentResult> RunAsync(
        EnvironmentAssessmentRequest request,
        WorkflowContext context,
        CancellationToken cancellationToken);
}

/// <summary>
/// Inspects an existing local project state and either resumes it (reusing
/// verified workspaces/materializations) or reports how it must be rebuilt.
/// This is the shared "continue existing project" entry point; hosts never
/// re-implement the verified reuse/recover decision.
/// </summary>
public interface IProjectStateWorkflow
{
    /// <summary>
    /// Classifies the workspace addressed by <paramref name="request.WorkspacePath"/>
    /// (or its sibling materialized root). Never produces disk data and never
    /// requires elevation; it only re-verifies existing local state.
    /// </summary>
    Task<ProjectStageStatus> InspectAsync(
        ProjectStateInspectRequest request,
        WorkflowContext context,
        CancellationToken cancellationToken);

    /// <summary>
    /// Resumes the project addressed by <paramref name="request.WorkspacePath"/>.
    /// Valid states are verified and reused; recoverable states are adopted;
    /// repairable states are repaired. A state that cannot be resumed throws a
    /// typed <see cref="AppFailureException"/> so the host can route to a
    /// "refresh from source" flow instead.
    /// </summary>
    Task<ProjectResumeResult> ResumeAsync(
        ProjectStateResumeRequest request,
        WorkflowContext context,
        CancellationToken cancellationToken);
}

/// <summary>
/// Inspects and (only after an explicit preview) reclaims application-owned
/// storage. Inventory and preview are read-only; cleanup removes only
/// independent transient and expired-recoverable objects, never user assets or
/// verified reusable workspaces. The <see cref="IProjectStateWorkflow"/> and
/// the workspace deletion boundary remain the authoritative reuse/delete path.
/// </summary>
public interface IStorageLifecycleWorkflow
{
    Task<StorageInventorySummary> InventoryAsync(
        StorageInventoryRequest request,
        WorkflowContext context,
        CancellationToken cancellationToken);

    Task<StorageCleanupPreview> PreviewCleanupAsync(
        StorageCleanupRequest request,
        WorkflowContext context,
        CancellationToken cancellationToken);

    Task<StorageCleanupResult> CleanupAsync(
        StorageCleanupRequest request,
        WorkflowContext context,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<DuplicateSnapshotGroup>> DuplicateSnapshotsAsync(
        StorageInventoryRequest request,
        WorkflowContext context,
        CancellationToken cancellationToken);

    Task<StorageCleanupPreview> PreviewDuplicateSnapshotCleanupAsync(
        StorageInventoryRequest request,
        WorkflowContext context,
        CancellationToken cancellationToken);

    Task<StorageCleanupResult> CleanupDuplicateSnapshotsAsync(
        StorageInventoryRequest request,
        WorkflowContext context,
        CancellationToken cancellationToken);
}

public sealed record StorageInventoryRequest(string? AppDataRoot = null);

public sealed record StorageCleanupRequest(
    bool ForceRecoverable = false,
    TimeSpan? RecoverableOlderThan = null,
    bool PruneOldSnapshots = false,
    string? AppDataRoot = null);

/// <summary>
/// Run / metadata retention for an export root's <c>runs/</c> directory.
/// Preview is read-only and never deletes; compact removes only the journal and
/// transaction metadata of older unreferenced runs while always retaining the
/// committed manifests, CSV, artifact index, and metadata-commit descriptor.
/// A run bound to a dataset selection profile is never compacted, and the
/// <c>latest</c> aliases are never the sole authority for what may be removed.
/// </summary>
public interface IRunRetentionWorkflow
{
    Task<RunRetentionPreview> PreviewAsync(
        RunRetentionOptions options,
        WorkflowContext context,
        CancellationToken cancellationToken);

    Task<RunRetentionResult> CompactAsync(
        RunRetentionOptions options,
        WorkflowContext context,
        CancellationToken cancellationToken);
}

public interface ISeedVcWorkflow
{
    Task<SeedVcDoctorReport> DoctorAsync(SeedVcDoctorRequest request, WorkflowContext context, CancellationToken cancellationToken);
    Task<SeedVcRemoteProbeReport> RemoteDoctorAsync(WorkflowContext context, CancellationToken cancellationToken);
    Task<SeedVcPrepareResult> PrepareAsync(SeedVcPrepareRequest request, WorkflowContext context, CancellationToken cancellationToken);
    Task<SeedVcTrainResult> TrainAsync(SeedVcTrainRequest request, WorkflowContext context, CancellationToken cancellationToken);
    Task<SeedVcInferResult> InferAsync(SeedVcInferRequest request, WorkflowContext context, CancellationToken cancellationToken);
}

public interface ISnapshotWorkflow
{
    Task<SnapshotWorkflowResult> RunAsync(
        SnapshotWorkflowRequest request,
        WorkflowContext context,
        CancellationToken cancellationToken);
}

public interface IMaterializationWorkflow
{
    Task<MaterializationWorkflowResult> RunAsync(
        MaterializationWorkflowRequest request,
        WorkflowContext context,
        CancellationToken cancellationToken);
}

public interface IWorkspaceWorkflow
{
    Task<WorkspaceCreateResult> CreateAsync(
        WorkspaceCreateRequest request,
        WorkflowContext context,
        CancellationToken cancellationToken);

    Task<VerifiedLocalWorkspace> VerifyAsync(
        string workspacePath,
        WorkflowContext context,
        CancellationToken cancellationToken);

    Task<VerifiedLocalWorkspace> RecoverMaterializationAsync(
        MaterializationRecoveryRequest request,
        WorkflowContext context,
        CancellationToken cancellationToken);

    Task<VerifiedLocalWorkspace> RepairMaterializationAsync(
        MaterializationRecoveryRequest request,
        WorkflowContext context,
        CancellationToken cancellationToken);

    Task<MaterializationRecoveryAssessment> AssessMaterializationRecoveryAsync(
        string outputDirectory,
        string? workspaceOutputPath,
        WorkflowContext context,
        CancellationToken cancellationToken);

    Task<WorkspaceDeletionPreview> PreviewDeleteMaterializedAsync(
        string workspacePath,
        WorkflowContext context,
        CancellationToken cancellationToken);

    Task<WorkspaceDeletionResult> DeleteMaterializedAsync(
        string workspacePath,
        WorkflowContext context,
        CancellationToken cancellationToken);
}

public interface IContactDiscoveryWorkflow
{
    Task<ContactDiscoveryResult> RunAsync(
        ContactDiscoveryRequest request,
        WorkflowContext context,
        CancellationToken cancellationToken);
}

public interface IVoiceScanWorkflow
{
    Task<VoiceScanWorkflowResult> RunAsync(
        VoiceScanWorkflowRequest request,
        WorkflowContext context,
        CancellationToken cancellationToken);
}

public interface IVoiceExportWorkflow
{
    Task<VoiceExportWorkflowResult> RunAsync(
        PreparedVoiceSelection plan,
        ExportDestination destination,
        WorkflowContext context,
        CancellationToken cancellationToken);

    Task<VoiceExportManifest> RecoverRunAsync(
        string journalPath,
        CancellationToken cancellationToken);

    Task<ExportVerificationResult> VerifyAsync(
        ExportVerificationRequest request,
        WorkflowContext context,
        CancellationToken cancellationToken);

    Task<ExportRepairResult> RepairAsync(
        ExportRepairRequest request,
        WorkflowContext context,
        CancellationToken cancellationToken);
}

// ---- Requests and results ----

public sealed record EnvironmentAssessmentRequest(string? WorkspacePath);

public sealed record EnvironmentAssessmentResult(
    bool IsWindows,
    IReadOnlyList<WeChatProcessInfo> RunningWeChatProcesses,
    IReadOnlyList<string> SupportedProcessNames,
    IReadOnlyList<KeyProfileMetadataModel> KeyAcquisitionProfiles,
    IReadOnlyList<string> MatchingKeyAcquisitionProfiles,
    IReadOnlyList<string> RegisteredAdapters,
    bool AdapterMatchEvaluated,
    IReadOnlyList<string> MatchingAdapters,
    bool WorkerInstalled,
    bool BrokerInstalled,
    bool BrokerAcquireAndMaterializeAvailable,
    VerifiedLocalWorkspace? Workspace,
    BrokerTrustResult? BrokerTrustResult = null,
    WorkerBundleTrustResult? WorkerBundleTrustResult = null,
    InstallDirectorySecurityResult? InstallDirectorySecurity = null);

public sealed record ProjectStateInspectRequest(string WorkspacePath, string? ExpectedAccountId = null);

public sealed record ProjectStateResumeRequest(
    string WorkspacePath,
    string? ExpectedAccountId = null,
    bool AutoRecover = true);

public sealed record ProjectResumeResult(
    ProjectStageState State,
    VerifiedLocalWorkspace Workspace,
    string WorkspacePath,
    bool RequiresElevation,
    bool ProducedNewDiskData);

public sealed record SnapshotWorkflowRequest(
    string SourceDirectory,
    string OutputDirectory,
    bool AllowLiveSource,
    int MaxAttempts = 3);

public sealed record SnapshotWorkflowResult(
    SnapshotManifest Manifest,
    SnapshotSourceIdentity? SourceIdentity,
    string ManifestPath);

public sealed record WorkspaceCreateRequest(string RootDirectory, string OutputPath);

public sealed record WorkspaceCreateResult(LocalWorkspace Workspace, string OutputPath);
public sealed record WorkspaceDeletionPreview(string WorkspaceId, string RootDirectory, int DatabaseCount, long TotalBytes);
public sealed record WorkspaceDeletionResult(
    string WorkspaceId,
    string RootDirectory,
    int DatabaseCount,
    long TotalBytes,
    string WorkspaceDocumentPath = "",
    bool WorkspaceDocumentDeleted = false,
    bool DurationCacheDeleted = true,
    bool DeepScanCacheDeleted = true);
public sealed record MaterializationRecoveryRequest(
    string OutputDirectory,
    string? WorkspaceOutputPath = null,
    string? AccountId = null);

public sealed record ContactDiscoveryRequest(
    string WorkspacePath,
    string? Username = null,
    string? SearchTerm = null);

public sealed record ContactDiscoveryResult(
    IReadOnlyList<ContactRecord> Contacts,
    VerifiedLocalWorkspace Workspace,
    string WorkspaceDocumentPath);

public sealed record VoiceScanWorkflowRequest(
    string WorkspacePath,
    string? ContactUsername = null,
    string? ConversationId = null,
    VoiceDirection? Direction = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    int? MaximumResults = null,
    bool DeepScan = false,
    bool ResolveDurations = false,
    string? ExpectedContactId = null,
    long? MinimumDurationMs = null,
    long? MaximumDurationMs = null,
    long? MinimumPayloadBytes = null,
    long? MaximumPayloadBytes = null);

public sealed record VoiceScanWorkflowResult(
    VoiceScanReport Report,
    VerifiedLocalWorkspace Workspace,
    PreparedVoiceSelection? Selection = null);

/// <summary>
/// Legacy request retained only as a source-compatible migration adapter for
/// callers that have not yet adopted the scan-then-export workflow. Desktop
/// and the public workflow interface use <see cref="PreparedVoiceSelection"/>
/// directly.
/// </summary>
public sealed record VoiceExportWorkflowRequest(
    string WorkspacePath,
    string OutputDirectory,
    string? ContactUsername = null,
    string? ConversationId = null,
    VoiceDirection? Direction = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    int? MaximumResults = null,
    string? ExpectedResultSetFingerprint = null,
    int? ExpectedResultCount = null,
    long? ExpectedTotalPayloadBytes = null,
    string? ExpectedContactId = null,
    long? MinimumDurationMs = null,
    long? MaximumDurationMs = null,
    long? MinimumPayloadBytes = null,
    long? MaximumPayloadBytes = null,
    bool ResolveDurations = false,
    ExportCompletionPolicy CompletionPolicy = ExportCompletionPolicy.ExactAllOrNothing);

public sealed record VoiceExportWorkflowResult(
    VoiceExportManifest Manifest,
    VerifiedLocalWorkspace Workspace);

public sealed record ExportVerificationRequest(
    string ExportDirectory,
    string? RunId = null);

public sealed record ExportRepairRequest(
    string ExportDirectory,
    string? RunId = null);
