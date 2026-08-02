using WeChatVoice.Core.Errors;
using WeChatVoice.Core.Models;
using WeChatVoice.Core.Ports;
using WeChatVoice.Workflows.Workflows;

namespace WeChatVoice.Desktop.Tests;

/// <summary>
/// Fake workflows for ViewModel tests. ViewModels never touch a real Weixin
/// process, SQLite database, or Key Broker implementation.
/// </summary>
public sealed class FakeEnvironmentWorkflow : IEnvironmentAssessmentWorkflow
{
    public EnvironmentAssessmentResult Result { get; set; } = new(
        IsWindows: true,
        RunningWeChatProcesses: [],
        SupportedProcessNames: ["WeChat.exe"],
        KeyAcquisitionProfiles: [],
        MatchingKeyAcquisitionProfiles: [],
        RegisteredAdapters: [],
        AdapterMatchEvaluated: false,
        MatchingAdapters: [],
        WorkerInstalled: true,
        BrokerInstalled: true,
        BrokerAcquireAndMaterializeAvailable: true,
        Workspace: null);

    public Exception? Throw { get; set; }
    public Func<CancellationToken, Task<EnvironmentAssessmentResult>>? RunOverride { get; set; }

    public Task<EnvironmentAssessmentResult> RunAsync(EnvironmentAssessmentRequest request, WorkflowContext context, CancellationToken cancellationToken)
        => RunOverride is not null ? RunOverride(cancellationToken) : Throw is null ? Task.FromResult(Result) : Task.FromException<EnvironmentAssessmentResult>(Throw);
}

public sealed class FakeMaterializationWorkflow : IMaterializationWorkflow
{
    public Exception? Throw { get; set; }

    public Action<WorkflowContext>? OnRun { get; set; }

    public MaterializationWorkflowResult Result { get; set; } = new(
        TestDoubles.Verified(),
        "C:\\out\\workspace.json",
        new AccountIdentity(AccountIdentityState.Candidate, null, UserConfirmationState.Confirmed),
        "profile-id",
        "materialization-id");

    public async Task<MaterializationWorkflowResult> RunAsync(MaterializationWorkflowRequest request, WorkflowContext context, CancellationToken cancellationToken)
    {
        OnRun?.Invoke(context);
        if (Throw is not null)
        {
            throw Throw;
        }

        // Mirror the real workflow: block on the host's confirmation port.
        var report = new AccountIdentityReport("wxid_owner", AccountIdentityState.Candidate, null);
        var confirmation = await context.AccountConfirmation.ConfirmAsync(report, cancellationToken).ConfigureAwait(false);
        if (!confirmation.Confirmed)
        {
            throw new AppFailureException(ErrorCode.AccountConfirmationRequired, "Account confirmation was declined.");
        }

        return Result;
    }
}

public sealed class FakeWorkspaceWorkflow : IWorkspaceWorkflow
{
    public MaterializationRecoveryAssessment Assessment { get; set; } = new(
        "C:\\out",
        MaterializationCommitStates.FailedRecoverable,
        CanRecover: true,
        WorkspaceDocumentPresent: false);

    public VerifiedLocalWorkspace RecoveryResult { get; set; } = TestDoubles.Verified();

    public MaterializationRecoveryRequest? LastRecoveryRequest { get; private set; }

    public Task<WorkspaceCreateResult> CreateAsync(WorkspaceCreateRequest request, WorkflowContext context, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<VerifiedLocalWorkspace> VerifyAsync(string workspacePath, WorkflowContext context, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<VerifiedLocalWorkspace> RecoverMaterializationAsync(MaterializationRecoveryRequest request, WorkflowContext context, CancellationToken cancellationToken)
    {
        LastRecoveryRequest = request;
        return Task.FromResult(RecoveryResult);
    }

    public Task<MaterializationRecoveryAssessment> AssessMaterializationRecoveryAsync(string outputDirectory, string? workspaceOutputPath, WorkflowContext context, CancellationToken cancellationToken)
        => Task.FromResult(Assessment with { OutputDirectory = Path.GetFullPath(outputDirectory) });

    public Task<WorkspaceDeletionPreview> PreviewDeleteMaterializedAsync(string workspacePath, WorkflowContext context, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<WorkspaceDeletionResult> DeleteMaterializedAsync(string workspacePath, WorkflowContext context, CancellationToken cancellationToken)
        => throw new NotSupportedException();
}

public sealed class FakeSnapshotWorkflow : ISnapshotWorkflow
{
    public SnapshotWorkflowRequest? LastRequest { get; private set; }

    public Task<SnapshotWorkflowResult> RunAsync(
        SnapshotWorkflowRequest request,
        WorkflowContext context,
        CancellationToken cancellationToken)
    {
        LastRequest = request;
        var manifest = new SnapshotManifest(
            request.SourceDirectory,
            request.OutputDirectory,
            DateTimeOffset.UtcNow,
            Files: []);
        return Task.FromResult(new SnapshotWorkflowResult(
            manifest,
            new SnapshotSourceIdentity("wxid_owner_0000000000000000", "wxid_owner", null, 1),
            Path.Combine(request.OutputDirectory, ".wechatvoice", "snapshot-manifest.json")));
    }
}

public sealed class FakeContactWorkflow : IContactDiscoveryWorkflow
{
    public ContactDiscoveryRequest? LastRequest { get; private set; }

    public Task<ContactDiscoveryResult> RunAsync(ContactDiscoveryRequest request, WorkflowContext context, CancellationToken cancellationToken)
    {
        LastRequest = request;
        return Task.FromResult(new ContactDiscoveryResult(
            [
                new ContactRecord("contact-a", "wxid_a", "A", "Remark A", Nickname: "Nick A"),
                new ContactRecord("contact-b", "wxid_b", "B", "Remark B", Nickname: "Nick B"),
            ],
            TestDoubles.Verified(),
            Path.GetFullPath(request.WorkspacePath)));
    }
}

public sealed class FakeScanWorkflow : IVoiceScanWorkflow
{
    public VoiceScanWorkflowResult Result { get; set; } = new(
        new VoiceScanReport(
            MatchedVoiceCount: 4,
            TotalDurationMs: 400,
            EarliestOccurredAtUtc: null,
            LatestOccurredAtUtc: null,
            ShardCounts: new Dictionary<string, int>(),
            UnassociatedMediaCount: 1,
            EmptyBlobCount: 1,
            SuspectedDuplicateCount: 0,
            InvalidHeaderCount: 1,
            AmbiguousPayloadCount: 1,
            ExportableVoiceCount: 1,
            TotalPayloadBytes: 10,
            ResultSetFingerprint: "fake-result-set"),
        TestDoubles.Verified());

    public VoiceScanWorkflowRequest? LastRequest { get; private set; }

    public Func<CancellationToken, Task<VoiceScanWorkflowResult>>? RunOverride { get; set; }

    public Task<VoiceScanWorkflowResult> RunAsync(VoiceScanWorkflowRequest request, WorkflowContext context, CancellationToken cancellationToken)
    {
        LastRequest = request;
        return RunOverride is not null ? RunOverride(cancellationToken) : Task.FromResult(Result);
    }
}

public sealed class FakeExportWorkflow : IVoiceExportWorkflow
{
    public VoiceExportWorkflowRequest? LastRequest { get; private set; }

    public VoiceExportManifest Manifest { get; set; } = new(
        DateTimeOffset.UtcNow,
        Entries: [new VoiceExportEntry("m1", "wxid_peer", DateTimeOffset.UtcNow, VoiceDirection.Incoming, "original/x.silk", 10, "aa", null, SourceStableKey: "k", WasSkipped: false)],
        Failures: [new VoiceExportFailure("m2", "payload-invalid-header", "invalid header")],
        RunId: "run-1",
        RunStatus: ExportRunStatus.CompletedWithFailures);

    public Task<VoiceExportWorkflowResult> RunAsync(VoiceExportWorkflowRequest request, WorkflowContext context, CancellationToken cancellationToken)
    {
        LastRequest = request;
        return Task.FromResult(new VoiceExportWorkflowResult(Manifest, TestDoubles.Verified()));
    }

    public Task<VoiceExportManifest> RecoverRunAsync(string journalPath, CancellationToken cancellationToken)
        => Task.FromResult(Manifest);
}

public static class TestDoubles
{
    public static LocalWorkspace Workspace()
        => new LocalWorkspace(
            "workspace-fake",
            "C:\\fake\\root",
            new WeChatDataSet("dataset-fake", "wxid_owner", [], "snapshot-fake", "fake-adapter"),
            DateTimeOffset.UtcNow,
            Issues: [],
            AdapterCandidates: []);

    public static VerifiedLocalWorkspace Verified() => new(Workspace(), DateTimeOffset.UtcNow);

    public sealed class SilentConfirmation : IAccountConfirmation
    {
        public Task<AccountConfirmation> ConfirmAsync(AccountIdentityReport report, CancellationToken cancellationToken)
            => Task.FromResult(new AccountConfirmation(true, report.AccountCandidate));
    }
}
