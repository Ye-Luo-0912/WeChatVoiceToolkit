using WeChatVoice.Core.Errors;
using WeChatVoice.Core.Models;
using WeChatVoice.Infrastructure.Materialization;
using WeChatVoice.Infrastructure.Sqlite;

namespace WeChatVoice.Workflows.Workflows;

/// <summary>
/// Creates and verifies local workspaces. Creation probes a decrypted
/// database root and retains local paths; verification re-probes and hashes
/// the group so a workspace never points at changed databases.
/// </summary>
public sealed class WorkspaceWorkflow : IWorkspaceWorkflow
{
    private readonly LocalWorkspaceCreator _creator;
    private readonly Workspaces.WorkspaceLoader _loader;
    private readonly MaterializationRecoveryService _recovery;

    public WorkspaceWorkflow(
        LocalWorkspaceCreator? creator = null,
        Workspaces.WorkspaceLoader? loader = null,
        MaterializationRecoveryService? recovery = null)
    {
        _creator = creator ?? new LocalWorkspaceCreator();
        _loader = loader ?? new Workspaces.WorkspaceLoader();
        _recovery = recovery ?? new MaterializationRecoveryService();
    }

    public async Task<WorkspaceCreateResult> CreateAsync(
        WorkspaceCreateRequest request,
        WorkflowContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        if (!context.TryStart())
        {
            throw new InvalidOperationException("The workflow state machine is not idle.");
        }

        try
        {
            context.Report(OperationPhase.Workspace, OperationStageIds.Preparing);
            var workspace = await _creator.CreateAsync(request.RootDirectory, cancellationToken).ConfigureAwait(false);
            var fullOutputPath = Path.GetFullPath(request.OutputPath);
            var directory = Path.GetDirectoryName(fullOutputPath)
                ?? throw new ArgumentException("The output path must include a directory.", nameof(request));
            Directory.CreateDirectory(directory);
            await LocalWorkspaceDocumentStore.WriteAsync(fullOutputPath, workspace, cancellationToken).ConfigureAwait(false);
            context.StateMachine.TryComplete();
            context.Report(OperationPhase.Workspace, OperationStageIds.Completing);
            return new WorkspaceCreateResult(workspace, fullOutputPath);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            context.StateMachine.TryCancel();
            throw;
        }
        catch
        {
            context.StateMachine.TryFail();
            throw;
        }
    }

    public async Task<VerifiedLocalWorkspace> VerifyAsync(
        string workspacePath,
        WorkflowContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.TryStart())
        {
            throw new InvalidOperationException("The workflow state machine is not idle.");
        }

        try
        {
            context.Report(OperationPhase.Workspace, OperationStageIds.VerifyingWorkspace);
            var verified = await _loader.LoadVerifiedAsync(workspacePath, cancellationToken).ConfigureAwait(false);
            context.StateMachine.TryComplete();
            context.Report(OperationPhase.Workspace, OperationStageIds.Completing);
            return verified;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            context.StateMachine.TryCancel();
            throw;
        }
        catch
        {
            context.StateMachine.TryFail();
            throw;
        }
    }

    public async Task<VerifiedLocalWorkspace> RecoverMaterializationAsync(
        MaterializationRecoveryRequest request,
        WorkflowContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        if (!context.TryStart())
        {
            throw new InvalidOperationException("The workflow state machine is not idle.");
        }

        try
        {
            context.Report(OperationPhase.Workspace, OperationStageIds.VerifyingWorkspace, "正在验证物料化提交状态");
            var outputRoot = Path.GetFullPath(request.OutputDirectory);
            var workspacePath = Path.GetFullPath(request.WorkspaceOutputPath ?? Path.Combine(
                Path.GetDirectoryName(outputRoot) ?? throw new InvalidDataException("The materialization output has no parent directory."),
                Path.GetFileName(outputRoot) + ".workspace.json"));
            var manifest = await MaterializationRecoveryService.ReadAndVerifyManifestAsync(outputRoot, cancellationToken).ConfigureAwait(false);
            var existingWorkspace = File.Exists(workspacePath)
                ? await _loader.ReadAsync(workspacePath, cancellationToken).ConfigureAwait(false)
                : null;
            var candidate = manifest.AccountId ?? existingWorkspace?.DataSet.AccountId ?? request.AccountId;
            var accountId = request.AccountId ?? existingWorkspace?.AccountIdentity.ConfirmedAccountId;
            AccountIdentity? recoveryIdentity = existingWorkspace?.AccountIdentity;

            if (accountId is null && existingWorkspace?.AccountIdentity.UserConfirmation == UserConfirmationState.Confirmed)
            {
                accountId = existingWorkspace.DataSet.AccountId;
            }

            if (manifest.AccountEvidenceState == AccountEvidenceState.DatabaseConfirmed && candidate is not null)
            {
                accountId ??= candidate;
                recoveryIdentity = new AccountIdentity(
                    AccountIdentityState.Confirmed,
                    null,
                    UserConfirmationState.NotConfirmed,
                    accountId);
            }
            else if (candidate is not null
                && (recoveryIdentity?.UserConfirmation != UserConfirmationState.Confirmed
                    || !string.Equals(recoveryIdentity.ConfirmedAccountId, candidate, StringComparison.Ordinal)))
            {
                context.StateMachine.TryEnterAwaitingUser();
                context.Report(OperationPhase.Workspace, OperationStageIds.ConfirmingAccount, "恢复前需要再次确认账号");
                try
                {
                    var confirmation = await context.AccountConfirmation.ConfirmAsync(
                        new AccountIdentityReport(candidate, AccountIdentityState.Candidate, null),
                        cancellationToken).ConfigureAwait(false);
                    if (!confirmation.Confirmed
                        || !string.Equals(confirmation.ConfirmedAccountId, candidate, StringComparison.Ordinal))
                    {
                        throw new AppFailureException(ErrorCode.AccountConfirmationRequired, "Account confirmation was declined during recovery.");
                    }

                    accountId = candidate;
                    recoveryIdentity = new AccountIdentity(
                        AccountIdentityState.Candidate,
                        null,
                        UserConfirmationState.Confirmed,
                        candidate);
                }
                finally
                {
                    context.StateMachine.TryResumeFromUser();
                }
            }

            var verified = await _recovery.RecoverAsync(
                outputRoot,
                workspacePath,
                accountId,
                cancellationToken,
                recoveryIdentity).ConfigureAwait(false);
            context.StateMachine.TryComplete();
            context.Report(OperationPhase.Workspace, OperationStageIds.Completing, "物料化恢复完成");
            return verified;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            context.StateMachine.TryCancel();
            throw;
        }
        catch
        {
            context.StateMachine.TryFail();
            throw;
        }
    }

    public async Task<VerifiedLocalWorkspace> RepairMaterializationAsync(
        MaterializationRecoveryRequest request,
        WorkflowContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        if (!context.TryStart())
        {
            throw new InvalidOperationException("The workflow state machine is not idle.");
        }

        try
        {
            context.Report(OperationPhase.Workspace, OperationStageIds.VerifyingWorkspace, "正在验证并修复 Workspace 文档");
            var outputRoot = Path.GetFullPath(request.OutputDirectory);
            var workspacePath = Path.GetFullPath(request.WorkspaceOutputPath ?? Path.Combine(
                Path.GetDirectoryName(outputRoot) ?? throw new InvalidDataException("The materialization output has no parent directory."),
                Path.GetFileName(outputRoot) + ".workspace.json"));
            var manifest = await MaterializationRecoveryService.ReadAndVerifyManifestAsync(outputRoot, cancellationToken).ConfigureAwait(false);
            var accountId = request.AccountId ?? manifest.AccountId;
            AccountIdentity? identity = null;
            if (manifest.AccountEvidenceState == AccountEvidenceState.DatabaseConfirmed)
            {
                identity = new AccountIdentity(AccountIdentityState.Confirmed, null, UserConfirmationState.NotConfirmed, accountId);
            }
            else if (!string.IsNullOrWhiteSpace(accountId))
            {
                context.StateMachine.TryEnterAwaitingUser();
                context.Report(OperationPhase.Workspace, OperationStageIds.ConfirmingAccount, "修复前需要确认账号");
                try
                {
                    var confirmation = await context.AccountConfirmation.ConfirmAsync(
                        new AccountIdentityReport(accountId, AccountIdentityState.Candidate, null),
                        cancellationToken).ConfigureAwait(false);
                    if (!confirmation.Confirmed || !string.Equals(confirmation.ConfirmedAccountId, accountId, StringComparison.Ordinal))
                    {
                        throw new AppFailureException(ErrorCode.AccountConfirmationRequired, "Account confirmation was declined during workspace repair.");
                    }

                    identity = new AccountIdentity(AccountIdentityState.Candidate, null, UserConfirmationState.Confirmed, accountId);
                }
                finally
                {
                    context.StateMachine.TryResumeFromUser();
                }
            }

            var verified = await _recovery.RepairWorkspaceAsync(outputRoot, workspacePath, accountId, cancellationToken, identity).ConfigureAwait(false);
            context.StateMachine.TryComplete();
            context.Report(OperationPhase.Workspace, OperationStageIds.Completing, "Workspace 文档修复完成");
            return verified;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            context.StateMachine.TryCancel();
            throw;
        }
        catch
        {
            context.StateMachine.TryFail();
            throw;
        }
    }

    public async Task<MaterializationRecoveryAssessment> AssessMaterializationRecoveryAsync(
        string outputDirectory,
        string? workspaceOutputPath,
        WorkflowContext context,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(context);
        if (!context.TryStart())
        {
            throw new InvalidOperationException("The workflow state machine is not idle.");
        }

        try
        {
            context.Report(OperationPhase.Workspace, OperationStageIds.VerifyingWorkspace, "正在检查可恢复物料化");
            var assessment = await _recovery.AssessAsync(outputDirectory, workspaceOutputPath, cancellationToken).ConfigureAwait(false);
            context.StateMachine.TryComplete();
            context.Report(OperationPhase.Workspace, OperationStageIds.Completing);
            return assessment;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            context.StateMachine.TryCancel();
            throw;
        }
        catch
        {
            context.StateMachine.TryFail();
            throw;
        }
    }

    public Task<WorkspaceDeletionPreview> PreviewDeleteMaterializedAsync(string workspacePath, WorkflowContext context, CancellationToken cancellationToken)
        => new DeleteMaterializedWorkspaceWorkflow().PreviewAsync(workspacePath, context, cancellationToken);

    public Task<WorkspaceDeletionResult> DeleteMaterializedAsync(string workspacePath, WorkflowContext context, CancellationToken cancellationToken)
        => new DeleteMaterializedWorkspaceWorkflow().RunAsync(workspacePath, context, cancellationToken);

}
