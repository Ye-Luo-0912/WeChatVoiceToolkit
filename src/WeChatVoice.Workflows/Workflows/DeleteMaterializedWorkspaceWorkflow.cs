using WeChatVoice.Core.Errors;
using WeChatVoice.Core.Models;
using WeChatVoice.Infrastructure.Materialization;
using WeChatVoice.Infrastructure.Sqlite;

namespace WeChatVoice.Workflows.Workflows;

/// <summary>
/// Revalidates the complete materialization boundary before deletion. Preview
/// and delete share the same inspection path; delete repeats the inspection
/// while holding the materialization lock so a preview can never authorize a
/// different output tree.
/// </summary>
public sealed class DeleteMaterializedWorkspaceWorkflow : IDeleteMaterializedWorkspaceWorkflow
{
    private readonly Workspaces.WorkspaceLoader _loader = new();

    public async Task<WorkspaceDeletionPreview> PreviewAsync(
        string workspacePath,
        WorkflowContext context,
        CancellationToken cancellationToken)
    {
        if (!context.TryStart()) throw new InvalidOperationException("The workflow state machine is not idle.");
        try
        {
            var candidate = await ReadCandidateAsync(workspacePath, cancellationToken).ConfigureAwait(false);
            await using var stateLock = await MaterializationStateStore.AcquireLockAsync(candidate.RootDirectory, cancellationToken).ConfigureAwait(false);
            var inspection = await InspectAsync(candidate, cancellationToken).ConfigureAwait(false);
            context.StateMachine.TryComplete();
            return inspection.Preview;
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

    public async Task<WorkspaceDeletionResult> RunAsync(
        string workspacePath,
        WorkflowContext context,
        CancellationToken cancellationToken)
    {
        if (!context.TryStart()) throw new InvalidOperationException("The workflow state machine is not idle.");
        MaterializationStateLock? stateLock = null;
        try
        {
            var candidate = await ReadCandidateAsync(workspacePath, cancellationToken).ConfigureAwait(false);
            stateLock = await MaterializationStateStore.AcquireLockAsync(candidate.RootDirectory, cancellationToken).ConfigureAwait(false);
            var inspection = await InspectAsync(candidate, cancellationToken).ConfigureAwait(false);
            Directory.Delete(candidate.RootDirectory, recursive: true);
            var workspaceDocumentDeleted = false;
            if (File.Exists(candidate.WorkspacePath))
            {
                File.Delete(candidate.WorkspacePath);
                workspaceDocumentDeleted = true;
            }
            context.StateMachine.TryComplete();
            return new WorkspaceDeletionResult(
                inspection.Preview.WorkspaceId,
                inspection.Preview.RootDirectory,
                inspection.Preview.DatabaseCount,
                inspection.Preview.TotalBytes,
                candidate.WorkspacePath,
                workspaceDocumentDeleted,
                DurationCacheDeleted: true,
                DeepScanCacheDeleted: true);
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
        finally
        {
            if (stateLock is not null)
            {
                await stateLock.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<WorkspaceCandidate> ReadCandidateAsync(
        string workspacePath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        var fullWorkspacePath = Path.GetFullPath(workspacePath);
        var candidate = await _loader.ReadAsync(fullWorkspacePath, cancellationToken).ConfigureAwait(false);
        var root = Path.GetFullPath(candidate.SourceRoot);
        PathOverlapGuard.EnsureDisjoint(root, fullWorkspacePath);
        var rootInfo = new DirectoryInfo(root);
        if (!rootInfo.Exists || (rootInfo.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new AppFailureException(ErrorCode.MaterializationInvalid, "Materialized workspace root must not be a reparse point.");
        }

        WorkspacePathSafety.EnsureNoReparsePoints(root);
        return new WorkspaceCandidate(fullWorkspacePath, root);
    }

    private async Task<DeletionInspection> InspectAsync(
        WorkspaceCandidate candidate,
        CancellationToken cancellationToken)
    {
        var state = await MaterializationStateStore.ReadAsync(candidate.RootDirectory, cancellationToken).ConfigureAwait(false);
        if (state.State is not (MaterializationCommitStates.Completed or MaterializationCommitStates.FailedRecoverable))
        {
            throw new AppFailureException(ErrorCode.MaterializationInvalid, "Only a committed or recoverable materialized workspace may be deleted.");
        }

        // Reuse the same manifest validator as recovery. It rejects missing,
        // tampered, extra, duplicate, and reparse-point output before deletion.
        var manifest = await MaterializationRecoveryService.ReadAndVerifyManifestAsync(candidate.RootDirectory, cancellationToken).ConfigureAwait(false);
        var workspace = await _loader.LoadVerifiedAsync(candidate.WorkspacePath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(workspace.Workspace.SourceRoot, candidate.RootDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new AppFailureException(ErrorCode.MaterializationInvalid, "Workspace source root does not match its materialization root.");
        }

        var databaseFiles = manifest.Databases
            .Where(static item => item.Status is MaterializationDatabaseStatus.Materialized or MaterializationDatabaseStatus.CopiedAsPlaintext)
            .Select(static item => item.OutputRelativePath)
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var covered = manifest.Files
            .Where(file => databaseFiles.Contains(file.OutputRelativePath))
            .ToArray();
        if (covered.Length != databaseFiles.Count)
        {
            throw new AppFailureException(ErrorCode.MaterializationInvalid, "The materialization manifest does not cover every database before deletion.");
        }

        return new DeletionInspection(new WorkspaceDeletionPreview(
            workspace.Workspace.WorkspaceId,
            candidate.RootDirectory,
            covered.Length,
            covered.Sum(static file => file.ByteLength)));
    }

    private sealed record WorkspaceCandidate(string WorkspacePath, string RootDirectory);

    private sealed record DeletionInspection(WorkspaceDeletionPreview Preview);
}

public interface IDeleteMaterializedWorkspaceWorkflow
{
    Task<WorkspaceDeletionPreview> PreviewAsync(
        string workspacePath,
        WorkflowContext context,
        CancellationToken cancellationToken);

    Task<WorkspaceDeletionResult> RunAsync(
        string workspacePath,
        WorkflowContext context,
        CancellationToken cancellationToken);
}
