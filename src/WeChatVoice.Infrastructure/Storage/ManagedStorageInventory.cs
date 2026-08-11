using WeChatVoice.Core.Models;
using WeChatVoice.Infrastructure.Materialization;

namespace WeChatVoice.Infrastructure.Storage;

/// <summary>
/// The application-owned storage roots that inventory may scan. Only these
/// well-known roots are ever inspected; cleanup never follows a path outside
/// them.
/// </summary>
public sealed record StorageRoots(
    string AppDataRoot,
    string? TempRoot = null)
{
    public string SnapshotsRoot => Path.Combine(AppDataRoot, "Data", "Snapshots");

    public string WorkspacesRoot => Path.Combine(AppDataRoot, "Data", "Workspaces");

    public string? PreparedSelectionRoot => TempRoot is null ? null : Path.Combine(TempRoot, "prepared-selection");
}

/// <summary>
/// Read-only scanner that classifies application-owned storage objects by
/// ownership (<see cref="StorageAssetKind"/>) and totals their sizes. The
/// scanner never deletes anything; it only inspects known app roots and rejects
/// reparse points. It must stay read-only per the project guardrails.
/// </summary>
public sealed class ManagedStorageInventory
{
    private readonly StorageRoots _roots;

    public ManagedStorageInventory(StorageRoots roots)
    {
        _roots = roots;
    }

    /// <summary>
    /// Scans the app-owned roots and returns a per-category summary plus the
    /// individual objects. Export and Dataset sizes are intentionally reported
    /// as zero in this version: they are user-owned paths, not app-owned roots,
    /// and ownership must never be inferred from the Recent index.
    /// </summary>
    public async Task<StorageInventorySummary> InventoryAsync(CancellationToken cancellationToken)
    {
        var assets = new List<StorageAssetRecord>();

        await ScanSnapshotsAsync(assets, cancellationToken).ConfigureAwait(false);
        await ScanWorkspacesAsync(assets, cancellationToken).ConfigureAwait(false);
        ScanTransientRoots(assets);

        long snapshot = 0, workspace = 0, temp = 0, recoverable = 0, reclaimable = 0;
        foreach (var asset in assets)
        {
            switch (asset.Kind)
            {
                case StorageAssetKind.ReusableIntermediate when asset.Note == "snapshot":
                    snapshot += asset.TotalBytes;
                    break;
                case StorageAssetKind.ReusableIntermediate when asset.Note == "workspace":
                    workspace += asset.TotalBytes;
                    break;
                case StorageAssetKind.RecoverableIntermediate:
                    recoverable += asset.TotalBytes;
                    workspace += asset.TotalBytes;
                    break;
                case StorageAssetKind.Transient:
                    temp += asset.TotalBytes;
                    break;
            }

            if (asset.Kind is StorageAssetKind.Transient or StorageAssetKind.RecoverableIntermediate)
            {
                reclaimable += asset.TotalBytes;
            }
        }

        return new StorageInventorySummary(
            snapshot,
            workspace,
            ExportBytes: 0,
            DatasetBytes: 0,
            temp,
            recoverable,
            reclaimable,
            assets);
    }

    private async Task ScanSnapshotsAsync(List<StorageAssetRecord> assets, CancellationToken cancellationToken)
    {
        var snapshotRoot = _roots.SnapshotsRoot;
        if (!Directory.Exists(snapshotRoot) || IsReparsePoint(snapshotRoot))
        {
            return;
        }

        foreach (var accountDirectory in Directory.EnumerateDirectories(snapshotRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsReparsePoint(accountDirectory))
            {
                continue;
            }

            foreach (var operation in Directory.EnumerateDirectories(accountDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsReparsePoint(operation))
                {
                    continue;
                }

                var hasManifest = File.Exists(Path.Combine(operation, ".wechatvoice", "snapshot-manifest.json"));
                var kind = hasManifest ? StorageAssetKind.ReusableIntermediate : StorageAssetKind.Transient;
                assets.Add(new StorageAssetRecord(
                    kind,
                    operation,
                    GetDirectorySize(operation),
                    WorkspaceId: null,
                    SnapshotId: null,
                    LastModifiedUtc: GetLastModifiedUtc(operation),
                    HasActiveLock: false,
                    Note: "snapshot"));
            }
        }
    }

    private async Task ScanWorkspacesAsync(List<StorageAssetRecord> assets, CancellationToken cancellationToken)
    {
        var workspacesRoot = _roots.WorkspacesRoot;
        if (!Directory.Exists(workspacesRoot) || IsReparsePoint(workspacesRoot))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(workspacesRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsReparsePoint(directory))
            {
                continue;
            }

            var hasActiveLock = File.Exists(Path.Combine(directory, ".wechatvoice", "materialization.lock"));
            StorageAssetKind kind;
            string? note;
            try
            {
                var state = await MaterializationStateStore.ReadAsync(directory, cancellationToken).ConfigureAwait(false);
                kind = state.State switch
                {
                    MaterializationCommitStates.Staging => StorageAssetKind.Transient,
                    MaterializationCommitStates.FailedRecoverable
                        or MaterializationCommitStates.DatabasesCommitted
                        or MaterializationCommitStates.WorkspaceCommitted => StorageAssetKind.RecoverableIntermediate,
                    _ => StorageAssetKind.ReusableIntermediate,
                };
                note = "workspace";
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                kind = StorageAssetKind.Transient;
                note = null;
            }

            assets.Add(new StorageAssetRecord(
                kind,
                directory,
                GetDirectorySize(directory),
                WorkspaceId: null,
                SnapshotId: null,
                LastModifiedUtc: GetLastModifiedUtc(directory),
                HasActiveLock: hasActiveLock,
                Note: note));
        }
    }

    private void ScanTransientRoots(List<StorageAssetRecord> assets)
    {
        if (_roots.TempRoot is null)
        {
            return;
        }

        AddTransientRoot(assets, _roots.PreparedSelectionRoot);
        AddTransientRoot(assets, Path.Combine(_roots.TempRoot, "Snapshots"));
        AddTransientRoot(assets, Path.Combine(_roots.TempRoot, "SnapshotsStaging"));
    }

    private static void AddTransientRoot(List<StorageAssetRecord> assets, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path) || IsReparsePoint(path))
        {
            return;
        }

        // Only report the top-level children as individual transient objects so
        // cleanup can remove them independently and safely.
        foreach (var child in Directory.EnumerateDirectories(path))
        {
            if (!IsReparsePoint(child))
            {
                assets.Add(new StorageAssetRecord(
                    StorageAssetKind.Transient,
                    child,
                    GetDirectorySize(child),
                    WorkspaceId: null,
                    SnapshotId: null,
                    LastModifiedUtc: GetLastModifiedUtc(child),
                    HasActiveLock: false,
                    Note: "temp"));
            }
        }
    }

    private static long GetDirectorySize(string path)
    {
        try
        {
            long total = 0;
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0)
                {
                    total += new FileInfo(file).Length;
                }
            }

            return total;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static DateTimeOffset GetLastModifiedUtc(string path)
    {
        try
        {
            return new DateTimeOffset(File.GetLastWriteTimeUtc(path));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return DateTimeOffset.MinValue;
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
}