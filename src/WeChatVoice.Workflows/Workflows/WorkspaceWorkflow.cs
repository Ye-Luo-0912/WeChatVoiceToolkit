using WeChatVoice.Core.Models;
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

    public WorkspaceWorkflow(LocalWorkspaceCreator? creator = null, Workspaces.WorkspaceLoader? loader = null)
    {
        _creator = creator ?? new LocalWorkspaceCreator();
        _loader = loader ?? new Workspaces.WorkspaceLoader();
    }

    public async Task<WorkspaceCreateResult> CreateAsync(
        WorkspaceCreateRequest request,
        WorkflowContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        if (!context.StateMachine.TryStart())
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
        if (!context.StateMachine.TryStart())
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
