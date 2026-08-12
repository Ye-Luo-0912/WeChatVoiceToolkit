using System.Text.Json;
using System.Text.Json.Serialization;
using WeChatVoice.Core.Models;

namespace WeChatVoice.Desktop.Infrastructure;

/// <summary>
/// Recent workspace metadata, stored only under LocalApplicationData. The
/// record holds workspace paths and stable ids so the UI can restore the last
/// used workspace without opening any database. It deliberately contains no
/// contact data, keys, or database content.
/// </summary>
public sealed record RecentWorkspaceEntry(
    string WorkspacePath,
    string WorkspaceId,
    string DataSetId,
    string? AccountId,
    DateTimeOffset LastUsedUtc,
    string? LastExportDirectory = null,
    string? MaterializedRootPath = null,
    string? LastContactUsername = null,
    string? LastContactId = null,
    RecentScanQuery? LastScanQuery = null,
    string? LastDatasetDirectory = null,
    string? LastPage = null);

public sealed record RecentScanQuery(
    string? Direction = null,
    string? FromUtc = null,
    string? ToUtc = null,
    int? MaximumResults = null,
    bool DeepScan = false,
    bool ResolveDurations = false,
    long? MinimumDurationMs = null,
    long? MaximumDurationMs = null,
    long? MinimumPayloadBytes = null,
    long? MaximumPayloadBytes = null);

public sealed record RecentSnapshotEntry(
    string SourceDirectory,
    string SnapshotDirectory,
    string SnapshotId,
    DateTimeOffset LastUsedUtc);

public sealed class RecentWorkspaceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _storePath;
    private readonly string _snapshotStorePath;
    private readonly object _gate = new();

    public RecentWorkspaceStore(string? directory = null)
    {
        var baseDirectory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WeChatVoiceToolkit");
        StorageDirectory = Path.GetFullPath(baseDirectory);
        _storePath = Path.Combine(baseDirectory, "recent-workspaces.json");
        _snapshotStorePath = Path.Combine(baseDirectory, "recent-snapshots.json");
    }

    /// <summary>Application-local metadata root; never contains raw database data.</summary>
    public string StorageDirectory { get; }

    public IReadOnlyList<RecentWorkspaceEntry> Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_storePath))
            {
                return [];
            }

            try
            {
                var json = File.ReadAllText(_storePath);
                return JsonSerializer.Deserialize<List<RecentWorkspaceEntry>>(json, JsonOptions) ?? [];
            }
            catch (JsonException)
            {
                return [];
            }
            catch (IOException)
            {
                return [];
            }
        }
    }

    public void Add(VerifiedLocalWorkspace workspace, string workspacePath)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        lock (_gate)
        {
            var fullPath = Path.GetFullPath(workspacePath);
            var existing = Load().FirstOrDefault(entry => string.Equals(entry.WorkspacePath, fullPath, StringComparison.OrdinalIgnoreCase));
            var entries = Load().Where(entry => !string.Equals(entry.WorkspacePath, fullPath, StringComparison.OrdinalIgnoreCase)).ToList();
            entries.Insert(0, existing is null
                ? new RecentWorkspaceEntry(
                    fullPath,
                    workspace.Workspace.WorkspaceId,
                    workspace.DataSet.DataSetId,
                    workspace.DataSet.AccountId,
                    DateTimeOffset.UtcNow,
                    MaterializedRootPath: workspace.Workspace.SourceRoot)
                : existing with
                {
                    WorkspaceId = workspace.Workspace.WorkspaceId,
                    DataSetId = workspace.DataSet.DataSetId,
                    AccountId = workspace.DataSet.AccountId,
                    LastUsedUtc = DateTimeOffset.UtcNow,
                    MaterializedRootPath = workspace.Workspace.SourceRoot,
                });
            while (entries.Count > 10)
            {
                entries.RemoveAt(entries.Count - 1);
            }

            Save(entries);
        }
    }

    public void Remove(string workspacePath)
    {
        lock (_gate)
        {
            Save(Load().Where(entry => !string.Equals(entry.WorkspacePath, Path.GetFullPath(workspacePath), StringComparison.OrdinalIgnoreCase)).ToList());
        }
    }

    public void SetLastExportDirectory(string workspacePath, string exportDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(exportDirectory);
        var fullWorkspacePath = Path.GetFullPath(workspacePath);
        var fullExportDirectory = Path.GetFullPath(exportDirectory);
        lock (_gate)
        {
            var entries = Load().Select(entry =>
                string.Equals(entry.WorkspacePath, fullWorkspacePath, StringComparison.OrdinalIgnoreCase)
                    ? entry with { LastExportDirectory = fullExportDirectory, LastUsedUtc = DateTimeOffset.UtcNow }
                    : entry).ToList();
            Save(entries);
        }
    }

    public void SetLastContact(string workspacePath, ContactRecord contact)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        ArgumentNullException.ThrowIfNull(contact);
        var full = Path.GetFullPath(workspacePath);
        lock (_gate)
        {
            Save(Load().Select(entry => string.Equals(entry.WorkspacePath, full, StringComparison.OrdinalIgnoreCase)
                ? entry with { LastContactUsername = contact.Username, LastContactId = contact.ContactId, LastUsedUtc = DateTimeOffset.UtcNow }
                : entry).ToList());
        }
    }

    public void SetLastScan(string workspacePath, ContactRecord contact, RecentScanQuery query)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        ArgumentNullException.ThrowIfNull(contact);
        ArgumentNullException.ThrowIfNull(query);
        var full = Path.GetFullPath(workspacePath);
        lock (_gate)
        {
            Save(Load().Select(entry => string.Equals(entry.WorkspacePath, full, StringComparison.OrdinalIgnoreCase)
                ? entry with
                {
                    LastContactUsername = contact.Username,
                    LastContactId = contact.ContactId,
                    LastScanQuery = query,
                    LastUsedUtc = DateTimeOffset.UtcNow,
                }
                : entry).ToList());
        }
    }

    public void SetLastDatasetDirectory(string workspacePath, string datasetDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetDirectory);
        var full = Path.GetFullPath(workspacePath);
        var dataset = Path.GetFullPath(datasetDirectory);
        lock (_gate)
        {
            Save(Load().Select(entry => string.Equals(entry.WorkspacePath, full, StringComparison.OrdinalIgnoreCase)
                ? entry with { LastDatasetDirectory = dataset, LastUsedUtc = DateTimeOffset.UtcNow }
                : entry).ToList());
        }
    }

    public void SetLastPage(string workspacePath, string pageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(pageId);
        var full = Path.GetFullPath(workspacePath);
        lock (_gate)
        {
            Save(Load().Select(entry => string.Equals(entry.WorkspacePath, full, StringComparison.OrdinalIgnoreCase)
                ? entry with { LastPage = pageId, LastUsedUtc = DateTimeOffset.UtcNow }
                : entry).ToList());
        }
    }

    public IReadOnlyList<RecentSnapshotEntry> LoadSnapshots()
    {
        lock (_gate)
        {
            if (!File.Exists(_snapshotStorePath))
            {
                return [];
            }

            try
            {
                var json = File.ReadAllText(_snapshotStorePath);
                return JsonSerializer.Deserialize<List<RecentSnapshotEntry>>(json, JsonOptions) ?? [];
            }
            catch (JsonException)
            {
                return [];
            }
            catch (IOException)
            {
                return [];
            }
        }
    }

    public void AddSnapshot(string sourceDirectory, string snapshotDirectory, string snapshotId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotId);
        lock (_gate)
        {
            var fullSource = Path.GetFullPath(sourceDirectory);
            var fullSnapshot = Path.GetFullPath(snapshotDirectory);
            var entries = LoadSnapshots()
                .Where(entry => !string.Equals(entry.SourceDirectory, fullSource, StringComparison.OrdinalIgnoreCase))
                .ToList();
            entries.Insert(0, new RecentSnapshotEntry(fullSource, fullSnapshot, snapshotId, DateTimeOffset.UtcNow));
            while (entries.Count > 10)
            {
                entries.RemoveAt(entries.Count - 1);
            }

            SaveSnapshots(entries);
        }
    }

    /// <summary>
    /// Removes recent entries that point to workspaces or snapshots which no
    /// longer exist on disk. The Recent list is a UX index, not ownership data;
    /// repair only drops dangling references and never deletes any workspace,
    /// snapshot, or export content. Returns the number of entries removed.
    /// </summary>
    public int RepairDangling()
    {
        lock (_gate)
        {
            var removed = 0;

            var workspaces = Load();
            var repairedWorkspaces = new List<RecentWorkspaceEntry>(workspaces.Count);
            foreach (var entry in workspaces)
            {
                if (File.Exists(entry.WorkspacePath))
                {
                    repairedWorkspaces.Add(entry);
                }
                else
                {
                    removed++;
                }
            }

            if (repairedWorkspaces.Count != workspaces.Count)
            {
                Save(repairedWorkspaces);
            }

            var snapshots = LoadSnapshots();
            var repairedSnapshots = new List<RecentSnapshotEntry>(snapshots.Count);
            foreach (var entry in snapshots)
            {
                if (Directory.Exists(entry.SnapshotDirectory))
                {
                    repairedSnapshots.Add(entry);
                }
                else
                {
                    removed++;
                }
            }

            if (repairedSnapshots.Count != snapshots.Count)
            {
                SaveSnapshots(repairedSnapshots);
            }

            return removed;
        }
    }

    public bool HasSnapshotForSource(string sourceDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        var fullSource = Path.GetFullPath(sourceDirectory);
        return LoadSnapshots().Any(entry =>
            string.Equals(entry.SourceDirectory, fullSource, StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(entry.SnapshotDirectory));
    }

    public RecentSnapshotEntry? FindSnapshotForSource(string sourceDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        var fullSource = Path.GetFullPath(sourceDirectory);
        return LoadSnapshots().FirstOrDefault(entry =>
            string.Equals(entry.SourceDirectory, fullSource, StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(entry.SnapshotDirectory));
    }

    private void Save(IReadOnlyList<RecentWorkspaceEntry> entries)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
            File.WriteAllText(_storePath, JsonSerializer.Serialize(entries, JsonOptions));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void SaveSnapshots(IReadOnlyList<RecentSnapshotEntry> entries)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_snapshotStorePath)!);
            File.WriteAllText(_snapshotStorePath, JsonSerializer.Serialize(entries, JsonOptions));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
