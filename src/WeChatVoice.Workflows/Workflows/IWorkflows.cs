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
        VoiceExportWorkflowRequest request,
        WorkflowContext context,
        CancellationToken cancellationToken);

    Task<VoiceExportManifest> RecoverRunAsync(
        string journalPath,
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
public sealed record WorkspaceDeletionResult(string WorkspaceId, string RootDirectory, int DatabaseCount, long TotalBytes);
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
    VerifiedLocalWorkspace Workspace);

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
    bool ResolveDurations = false);

public sealed record VoiceExportWorkflowResult(
    VoiceExportManifest Manifest,
    VerifiedLocalWorkspace Workspace);
