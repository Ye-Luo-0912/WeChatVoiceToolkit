using System.Text.Json;
using WeChatVoice.Core.Errors;
using WeChatVoice.Core.Models;

namespace WeChatVoice.Workflows.Workflows;

public sealed class DeleteMaterializedWorkspaceWorkflow : IDeleteMaterializedWorkspaceWorkflow
{
    public async Task<WorkspaceDeletionResult> RunAsync(string workspacePath, WorkflowContext context, CancellationToken cancellationToken)
    {
        if (!context.TryStart()) throw new InvalidOperationException("The workflow state machine is not idle.");
        try
        {
            var workspace = await new Workspaces.WorkspaceLoader().LoadVerifiedAsync(workspacePath, cancellationToken).ConfigureAwait(false);
            var root = Path.GetFullPath(workspace.Workspace.SourceRoot);
            var rootInfo = new DirectoryInfo(root);
            if ((rootInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new AppFailureException(ErrorCode.MaterializationInvalid, "Materialized workspace root must not be a reparse point.");
            var manifestPath = Path.Combine(root, ".wechatvoice", "materialization-manifest.json");
            if (!File.Exists(manifestPath)) throw new AppFailureException(ErrorCode.MaterializationInvalid, "Materialization manifest is missing.");
            var dbs = workspace.DataSet.Databases.Select(item => Path.GetFullPath(item.LocalPath!)).ToArray();
            if (dbs.Any(path => !IsUnder(path, root) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
                throw new AppFailureException(ErrorCode.MaterializationInvalid, "Manifest database paths are outside the materialized root or use reparse points.");
            var bytes = dbs.Where(File.Exists).Sum(path => new FileInfo(path).Length);
            Directory.Delete(root, recursive: true);
            context.StateMachine.TryComplete();
            return new WorkspaceDeletionResult(workspace.Workspace.WorkspaceId, root, dbs.Length, bytes);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { context.StateMachine.TryCancel(); throw; }
        catch { context.StateMachine.TryFail(); throw; }
    }

    private static bool IsUnder(string path, string root)
        => path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}

public interface IDeleteMaterializedWorkspaceWorkflow
{
    Task<WorkspaceDeletionResult> RunAsync(string workspacePath, WorkflowContext context, CancellationToken cancellationToken);
}
