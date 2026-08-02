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
            await WriteJsonFileAsync(fullOutputPath, workspace, cancellationToken).ConfigureAwait(false);
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
            var verified = await _recovery.RecoverAsync(outputRoot, workspacePath, request.AccountId, cancellationToken).ConfigureAwait(false);
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

    public Task<WorkspaceDeletionResult> DeleteMaterializedAsync(string workspacePath, WorkflowContext context, CancellationToken cancellationToken)
        => new DeleteMaterializedWorkspaceWorkflow().RunAsync(workspacePath, context, cancellationToken);

    private static async Task WriteJsonFileAsync<T>(string outputPath, T value, CancellationToken cancellationToken)
    {
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
        {
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase) },
        };
        await using var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await System.Text.Json.JsonSerializer.SerializeAsync(stream, value, options, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
