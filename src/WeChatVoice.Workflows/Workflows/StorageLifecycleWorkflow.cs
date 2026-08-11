using WeChatVoice.Core.Models;
using WeChatVoice.Infrastructure.Materialization;
using WeChatVoice.Infrastructure.Storage;

namespace WeChatVoice.Workflows.Workflows;

/// <summary>
/// Shared storage lifecycle workflow. Inventory and preview are read-only and
/// never delete. Cleanup removes only independent, app-owned transient objects
/// and expired recoverable workspaces (through the workspace deletion
/// boundary). User assets, datasets, and verified reusable workspaces are never
/// auto-deleted. Delete paths are guarded against reparse points and active
/// materialization locks.
/// </summary>
public sealed class StorageLifecycleWorkflow : IStorageLifecycleWorkflow
{
    private static readonly TimeSpan DefaultRecoverableRetention = TimeSpan.FromDays(7);

    private readonly ManagedStorageInventory _inventory;
    private readonly IDeleteMaterializedWorkspaceWorkflow _deleteWorkspace;

    public StorageLifecycleWorkflow(
        ManagedStorageInventory? inventory = null,
        IDeleteMaterializedWorkspaceWorkflow? deleteWorkspace = null)
    {
        _inventory = inventory ?? new ManagedStorageInventory(DefaultRoots());
        _deleteWorkspace = deleteWorkspace ?? new DeleteMaterializedWorkspaceWorkflow();
    }

    public async Task<StorageInventorySummary> InventoryAsync(
        StorageInventoryRequest request,
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
            context.Report(OperationPhase.StorageLifecycle, OperationStageIds.ScanningStorage, "正在扫描应用自有存储");
            var summary = await _inventory.InventoryAsync(cancellationToken).ConfigureAwait(false);
            context.StateMachine.TryComplete();
            context.Report(OperationPhase.StorageLifecycle, OperationStageIds.Completing);
            return summary;
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

    public async Task<StorageCleanupPreview> PreviewCleanupAsync(
        StorageCleanupRequest request,
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
            context.Report(OperationPhase.StorageLifecycle, OperationStageIds.PreviewingCleanup, "正在预览可回收对象");
            var summary = await _inventory.InventoryAsync(cancellationToken).ConfigureAwait(false);
            var items = SelectReclaimable(summary, request).ToList();
            context.StateMachine.TryComplete();
            context.Report(OperationPhase.StorageLifecycle, OperationStageIds.Completing);
            return new StorageCleanupPreview(items.Count, items.Sum(static item => item.TotalBytes), items);
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

    public async Task<StorageCleanupResult> CleanupAsync(
        StorageCleanupRequest request,
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
            context.Report(OperationPhase.StorageLifecycle, OperationStageIds.CleaningStorage, "正在清理应用自有临时与可恢复对象");
            var summary = await _inventory.InventoryAsync(cancellationToken).ConfigureAwait(false);
            var candidates = SelectReclaimable(summary, request).ToList();
            var skipped = new List<string>();
            var deleted = 0;
            long deletedBytes = 0;

            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (candidate.HasActiveLock)
                {
                    skipped.Add("存在活动锁，跳过可恢复对象。");
                    continue;
                }

                if (IsReparsePoint(candidate.Path))
                {
                    skipped.Add("拒绝清除含 Reparse Point 的对象。");
                    continue;
                }

                if (candidate.Kind == StorageAssetKind.RecoverableIntermediate)
                {
                    var workspacePath = DeriveWorkspaceDocumentPath(candidate.Path);
                    if (!File.Exists(workspacePath))
                    {
                        skipped.Add("Workspace 文档缺失，跳过恢复窗口内的可恢复对象。");
                        continue;
                    }

                    try
                    {
                        var result = await _deleteWorkspace.RunAsync(workspacePath, context, cancellationToken).ConfigureAwait(false);
                        deleted += 1;
                        deletedBytes += candidate.TotalBytes;
                        _ = result;
                    }
                    catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or Core.Errors.AppFailureException)
                    {
                        skipped.Add("可恢复 Workspace 校验失败，未删除。");
                    }

                    continue;
                }

                if (TryDeleteDirectory(candidate.Path))
                {
                    deleted += 1;
                    deletedBytes += candidate.TotalBytes;
                }
                else
                {
                    skipped.Add("临时对象删除失败，已跳过。");
                }
            }

            context.StateMachine.TryComplete();
            context.Report(OperationPhase.StorageLifecycle, OperationStageIds.Completing, "存储清理完成");
            return new StorageCleanupResult(deleted, deletedBytes, skipped);
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

    public async Task<IReadOnlyList<DuplicateSnapshotGroup>> DuplicateSnapshotsAsync(
        StorageInventoryRequest request,
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
            context.Report(OperationPhase.StorageLifecycle, OperationStageIds.ScanningStorage, "正在检测重复快照");
            var groups = await _inventory.DetectDuplicateSnapshotsAsync(cancellationToken).ConfigureAwait(false);
            context.StateMachine.TryComplete();
            context.Report(OperationPhase.StorageLifecycle, OperationStageIds.Completing);
            return groups;
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

    private static IEnumerable<StorageAssetRecord> SelectReclaimable(
        StorageInventorySummary summary,
        StorageCleanupRequest request)
    {
        var cutoff = DateTime.UtcNow - (request.RecoverableOlderThan ?? DefaultRecoverableRetention);
        foreach (var asset in summary.Assets)
        {
            if (asset.Kind == StorageAssetKind.Transient)
            {
                yield return asset;
                continue;
            }

            if (asset.Kind == StorageAssetKind.RecoverableIntermediate
                && (request.ForceRecoverable || asset.LastModifiedUtc < cutoff))
            {
                yield return asset;
            }
        }
    }

    private static string DeriveWorkspaceDocumentPath(string workspaceDirectory)
    {
        var full = Path.GetFullPath(workspaceDirectory);
        var parent = Path.GetDirectoryName(full)
            ?? throw new InvalidOperationException("无法确定 Workspace 输出目录的父目录。");
        return Path.Combine(parent, Path.GetFileName(full) + ".workspace.json");
    }

    private static bool TryDeleteDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return true;
            }

            Directory.Delete(path, recursive: true);
            return !Directory.Exists(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static StorageRoots DefaultRoots()
    {
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WeChatVoiceToolkit");
        return new StorageRoots(appData, Path.Combine(Path.GetTempPath(), "WeChatVoiceToolkit"));
    }
}