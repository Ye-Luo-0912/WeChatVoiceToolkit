using System.Text.Json;
using WeChatVoice.Core.Models;
using WeChatVoice.Infrastructure.Materialization;
using WeChatVoice.Infrastructure.Serialization;

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

    public string ScanCacheRoot => Path.Combine(AppDataRoot, "Data", "scan-cache");

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
    private readonly ManagedStoragePathRegistry _registry;

    public ManagedStorageInventory(StorageRoots roots, ManagedStoragePathRegistry? registry = null)
    {
        _roots = roots;
        _registry = registry ?? new ManagedStoragePathRegistry(roots.AppDataRoot);
    }

    public string SnapshotRoot => Path.GetFullPath(_roots.SnapshotsRoot);

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
        ScanRegisteredUserRoots(assets, cancellationToken);
        ScanTransientRoots(assets);

        long snapshot = 0, workspace = 0, exports = 0, datasets = 0, temp = 0, recoverable = 0, reclaimable = 0;
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
                case StorageAssetKind.UserAsset:
                    exports += asset.TotalBytes;
                    break;
                case StorageAssetKind.DerivedUserAsset:
                    datasets += asset.TotalBytes;
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
            ExportBytes: exports,
            DatasetBytes: datasets,
            temp,
            recoverable,
            reclaimable,
            assets);
    }

    /// <summary>
    /// Detects snapshots that share the same content fingerprint
    /// (<see cref="SnapshotManifest.SnapshotId"/>). Only verified snapshots with
    /// a readable manifest are considered; a manifest that cannot be read is
    /// skipped rather than assumed to be a duplicate. The scan is read-only.
    /// </summary>
    public async Task<IReadOnlyList<DuplicateSnapshotGroup>> DetectDuplicateSnapshotsAsync(CancellationToken cancellationToken)
    {
        var snapshotRoot = _roots.SnapshotsRoot;
        if (!Directory.Exists(snapshotRoot) || IsReparsePoint(snapshotRoot))
        {
            return [];
        }

        var byId = new Dictionary<string, List<StorageAssetRecord>>(StringComparer.OrdinalIgnoreCase);
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

                var manifestPath = Path.Combine(operation, ".wechatvoice", "snapshot-manifest.json");
                if (!File.Exists(manifestPath))
                {
                    continue;
                }

                SnapshotManifest manifest;
                try
                {
                    await using var stream = new FileStream(
                        manifestPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        64 * 1024,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    manifest = await System.Text.Json.JsonSerializer.DeserializeAsync<SnapshotManifest>(
                        stream,
                        InfrastructureJson.Compact,
                        cancellationToken).ConfigureAwait(false) ?? throw new InvalidDataException("Snapshot manifest is empty.");
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or System.Text.Json.JsonException)
                {
                    continue;
                }

                var record = new StorageAssetRecord(
                    StorageAssetKind.ReusableIntermediate,
                    operation,
                    GetDirectorySize(operation),
                    WorkspaceId: null,
                    SnapshotId: manifest.SnapshotId,
                    LastModifiedUtc: GetLastModifiedUtc(operation),
                    HasActiveLock: false,
                    Note: "snapshot");
                if (!byId.TryGetValue(manifest.SnapshotId, out var list))
                {
                    list = [];
                    byId[manifest.SnapshotId] = list;
                }

                list.Add(record);
            }
        }

        return byId
            .Where(static pair => pair.Value.Count > 1)
            .Select(static pair => new DuplicateSnapshotGroup(
                pair.Key,
                pair.Value.OrderByDescending(static item => item.LastModifiedUtc).ToArray()))
            .OrderByDescending(static group => group.Copies.Sum(static copy => copy.TotalBytes))
            .ToArray();
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

                var manifestPath = Path.Combine(operation, ".wechatvoice", "snapshot-manifest.json");
                var hasManifest = File.Exists(manifestPath);
                var kind = hasManifest ? StorageAssetKind.ReusableIntermediate : StorageAssetKind.Transient;
                string? snapshotId = null;
                if (hasManifest)
                {
                    try
                    {
                        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
                        if (document.RootElement.TryGetProperty("snapshotId", out var id)
                            && id.ValueKind == JsonValueKind.String)
                        {
                            snapshotId = id.GetString();
                        }
                    }
                    catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
                    {
                        snapshotId = null;
                    }
                }
                assets.Add(new StorageAssetRecord(
                    kind,
                    operation,
                    GetDirectorySize(operation),
                    WorkspaceId: null,
                    SnapshotId: snapshotId,
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
        AddTransientRoot(assets, _roots.ScanCacheRoot);
        AddTransientRoot(assets, Path.Combine(_roots.TempRoot, "Snapshots"));
        AddTransientRoot(assets, Path.Combine(_roots.TempRoot, "SnapshotsStaging"));
    }

    private void ScanRegisteredUserRoots(List<StorageAssetRecord> assets, CancellationToken cancellationToken)
    {
        foreach (var registered in _registry.Load())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.GetFullPath(registered.Path);
            if (!Directory.Exists(path) || IsReparsePoint(path)) continue;
            // A registered path must not be counted again if a user selected an
            // app-owned root as an output directory.
            if (IsUnder(path, _roots.SnapshotsRoot) || IsUnder(path, _roots.WorkspacesRoot)) continue;
            assets.Add(new StorageAssetRecord(
                registered.Kind,
                path,
                GetDirectorySize(path),
                WorkspaceId: null,
                SnapshotId: null,
                LastModifiedUtc: GetLastModifiedUtc(path),
                HasActiveLock: false,
                Note: registered.Kind == StorageAssetKind.UserAsset ? "export" : "dataset"));
        }
    }

    private static bool IsUnder(string path, string root)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
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
