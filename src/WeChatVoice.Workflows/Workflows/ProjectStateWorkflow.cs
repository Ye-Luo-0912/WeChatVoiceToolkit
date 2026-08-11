using WeChatVoice.Core.Errors;
using WeChatVoice.Core.Models;
using WeChatVoice.Infrastructure.Materialization;
using WeChatVoice.Workflows.Workspaces;

namespace WeChatVoice.Workflows.Workflows;

/// <summary>
/// Shared "continue existing project" workflow. It inspects an existing local
/// workspace (and its sibling materialized root) and either reuses a verified
/// workspace, adopts a recoverable materialization, or repairs a lost workspace
/// document — without re-running Snapshot, UAC, or materialization. Hosts only
/// present the classification and the user's continue/refresh choice; they
/// never re-implement the verified reuse or recovery decision.
/// </summary>
public sealed class ProjectStateWorkflow : IProjectStateWorkflow
{
    private readonly WorkspaceLoader _loader;
    private readonly MaterializationRecoveryService _recovery;
    private readonly IWorkspaceWorkflow _workspace;

    public ProjectStateWorkflow(
        WorkspaceLoader? loader = null,
        MaterializationRecoveryService? recovery = null,
        IWorkspaceWorkflow? workspace = null)
    {
        _loader = loader ?? new WorkspaceLoader();
        _recovery = recovery ?? new MaterializationRecoveryService();
        _workspace = workspace ?? new WorkspaceWorkflow(loader: _loader);
    }

    public async Task<ProjectStageStatus> InspectAsync(
        ProjectStateInspectRequest request,
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
            context.Report(OperationPhase.ProjectState, OperationStageIds.InspectingState, "正在检查本地项目状态");
            var status = await InspectCoreAsync(request, context, cancellationToken).ConfigureAwait(false);
            context.StateMachine.TryComplete();
            context.Report(OperationPhase.ProjectState, OperationStageIds.Completing);
            return status;
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

    public async Task<ProjectResumeResult> ResumeAsync(
        ProjectStateResumeRequest request,
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
            context.Report(OperationPhase.ProjectState, OperationStageIds.ResumingState, "正在继续已有项目");
            var result = await ResumeCoreAsync(request, context, cancellationToken).ConfigureAwait(false);
            context.StateMachine.TryComplete();
            context.Report(OperationPhase.ProjectState, OperationStageIds.Completing, "项目状态已继续");
            return result;
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

    private async Task<ProjectStageStatus> InspectCoreAsync(
        ProjectStateInspectRequest request,
        WorkflowContext context,
        CancellationToken cancellationToken)
    {
        var workspacePath = Path.GetFullPath(request.WorkspacePath);
        var materializedRoot = DeriveMaterializedRoot(workspacePath);
        var workspacePresent = File.Exists(workspacePath);
        var rootPresent = materializedRoot is not null && Directory.Exists(materializedRoot);

        if (rootPresent)
        {
            var assessment = await _recovery.AssessAsync(materializedRoot!, workspacePath, cancellationToken).ConfigureAwait(false);
            if (string.Equals(assessment.State, MaterializationCommitStates.Staging, StringComparison.Ordinal))
            {
                return Busy(workspacePath, materializedRoot, "物料化正在进行中，请稍后重试。");
            }
        }

        if (workspacePresent)
        {
            try
            {
                var verified = await _loader.LoadVerifiedAsync(workspacePath, cancellationToken).ConfigureAwait(false);
                if (request.ExpectedAccountId is not null
                    && !string.Equals(verified.DataSet.AccountId, request.ExpectedAccountId, StringComparison.Ordinal))
                {
                    return Stale(workspacePath, materializedRoot, verified.DataSet.AccountId,
                        "此 Workspace 属于另一个账号，不能直接复用。");
                }

                return new ProjectStageStatus(
                    ProjectStageState.ValidReusable,
                    workspacePath,
                    materializedRoot,
                    verified.DataSet.AccountId,
                    Reason: null,
                    RequiresElevation: false,
                    ProducesNewDiskData: false,
                    VerifiedWorkspace: verified);
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                if (rootPresent)
                {
                    var assessment = await _recovery.AssessAsync(materializedRoot!, workspacePath, cancellationToken).ConfigureAwait(false);
                    if (string.Equals(assessment.State, MaterializationCommitStates.Completed, StringComparison.Ordinal))
                    {
                        return new ProjectStageStatus(
                            ProjectStageState.Invalid,
                            workspacePath,
                            materializedRoot,
                            null,
                            "Workspace 文档损坏或校验失败，可通过修复恢复。",
                            RequiresElevation: false,
                            ProducesNewDiskData: true);
                    }
                }

                return Invalid(workspacePath, materializedRoot, "Workspace 校验失败，需要重新创建。");
            }
        }

        if (rootPresent)
        {
            var assessment = await _recovery.AssessAsync(materializedRoot!, workspacePath, cancellationToken).ConfigureAwait(false);
            if (string.Equals(assessment.State, MaterializationCommitStates.Completed, StringComparison.Ordinal))
            {
                return new ProjectStageStatus(
                    ProjectStageState.Invalid,
                    workspacePath,
                    materializedRoot,
                    null,
                    "Workspace 文档缺失，可通过修复恢复。",
                    RequiresElevation: false,
                    ProducesNewDiskData: true);
            }

            if (assessment.CanRecover)
            {
                return new ProjectStageStatus(
                    ProjectStageState.Recoverable,
                    workspacePath,
                    materializedRoot,
                    null,
                    "检测到已完成但未提交的物料化，可恢复 Workspace，无需重新解密。",
                    RequiresElevation: false,
                    ProducesNewDiskData: true);
            }

            return Invalid(workspacePath, materializedRoot, "物料化状态不可恢复，需要从源重新创建。");
        }

        return new ProjectStageStatus(
            ProjectStageState.Missing,
            workspacePath,
            materializedRoot,
            null,
            "未找到可复用的本地状态，需要从源创建。",
            RequiresElevation: false,
            ProducesNewDiskData: true);
    }

    private async Task<ProjectResumeResult> ResumeCoreAsync(
        ProjectStateResumeRequest request,
        WorkflowContext context,
        CancellationToken cancellationToken)
    {
        var workspacePath = Path.GetFullPath(request.WorkspacePath);
        var materializedRoot = DeriveMaterializedRoot(workspacePath);
        var workspacePresent = File.Exists(workspacePath);

        if (materializedRoot is not null && Directory.Exists(materializedRoot))
        {
            var assessment = await _recovery.AssessAsync(materializedRoot, workspacePath, cancellationToken).ConfigureAwait(false);
            if (string.Equals(assessment.State, MaterializationCommitStates.Staging, StringComparison.Ordinal))
            {
                throw new AppFailureException(ErrorCode.OperationBusy, "物料化正在进行中，无法继续该项目。");
            }
        }

        VerifiedLocalWorkspace verified;
        if (workspacePresent)
        {
            try
            {
                verified = await _loader.LoadVerifiedAsync(workspacePath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                verified = await TryRepairAsync(workspacePath, materializedRoot, request, context, cancellationToken).ConfigureAwait(false);
                return new ProjectResumeResult(ProjectStageState.ValidReusable, verified, workspacePath, RequiresElevation: false, ProducedNewDiskData: true);
            }
        }
        else
        {
            if (materializedRoot is null || !Directory.Exists(materializedRoot))
            {
                throw new AppFailureException(ErrorCode.InvalidRequest, "未找到本地项目状态，请从源创建。");
            }

            var assessment = await _recovery.AssessAsync(materializedRoot, workspacePath, cancellationToken).ConfigureAwait(false);
            if (assessment.CanRecover)
            {
                if (!request.AutoRecover)
                {
                    throw new AppFailureException(ErrorCode.InvalidRequest, "项目处于可恢复状态但未启用自动恢复，请显式选择恢复。");
                }

                context.Report(OperationPhase.ProjectState, OperationStageIds.ResumingState, "正在恢复未提交的物料化");
                verified = await _workspace.RecoverMaterializationAsync(
                    new MaterializationRecoveryRequest(materializedRoot, workspacePath, request.ExpectedAccountId),
                    context,
                    cancellationToken).ConfigureAwait(false);
                return new ProjectResumeResult(ProjectStageState.ValidReusable, verified, workspacePath, RequiresElevation: false, ProducedNewDiskData: true);
            }

            verified = await TryRepairAsync(workspacePath, materializedRoot, request, context, cancellationToken).ConfigureAwait(false);
            return new ProjectResumeResult(ProjectStageState.ValidReusable, verified, workspacePath, RequiresElevation: false, ProducedNewDiskData: true);
        }

        if (request.ExpectedAccountId is not null
            && !string.Equals(verified.DataSet.AccountId, request.ExpectedAccountId, StringComparison.Ordinal))
        {
            throw new AppFailureException(ErrorCode.WorkspaceInvalid, "此 Workspace 属于另一个账号，不能继续。");
        }

        return new ProjectResumeResult(ProjectStageState.ValidReusable, verified, workspacePath, RequiresElevation: false, ProducedNewDiskData: false);
    }

    private async Task<VerifiedLocalWorkspace> TryRepairAsync(
        string workspacePath,
        string? materializedRoot,
        ProjectStateResumeRequest request,
        WorkflowContext context,
        CancellationToken cancellationToken)
    {
        if (materializedRoot is null || !Directory.Exists(materializedRoot))
        {
            throw new AppFailureException(ErrorCode.WorkspaceInvalid, "Workspace 无法复用且缺少物料化根目录，请从源重新创建。");
        }

        context.Report(OperationPhase.ProjectState, OperationStageIds.ResumingState, "正在修复 Workspace 文档");
        return await _workspace.RepairMaterializationAsync(
            new MaterializationRecoveryRequest(materializedRoot, workspacePath, request.ExpectedAccountId),
            context,
            cancellationToken).ConfigureAwait(false);
    }

    private static string? DeriveMaterializedRoot(string workspacePath)
    {
        var full = Path.GetFullPath(workspacePath);
        var directory = Path.GetDirectoryName(full);
        var fileName = Path.GetFileName(full);
        const string suffix = ".workspace.json";
        if (directory is null || !fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Path.Combine(directory, fileName[..^suffix.Length]);
    }

    private static ProjectStageStatus Busy(string workspacePath, string? materializedRoot, string reason)
        => new(ProjectStageState.Busy, workspacePath, materializedRoot, null, reason, RequiresElevation: false, ProducesNewDiskData: false);

    private static ProjectStageStatus Stale(string workspacePath, string? materializedRoot, string? accountId, string reason)
        => new(ProjectStageState.Stale, workspacePath, materializedRoot, accountId, reason, RequiresElevation: false, ProducesNewDiskData: false);

    private static ProjectStageStatus Invalid(string workspacePath, string? materializedRoot, string reason)
        => new(ProjectStageState.Invalid, workspacePath, materializedRoot, null, reason, RequiresElevation: false, ProducesNewDiskData: true);
}