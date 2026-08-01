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
    DateTimeOffset LastUsedUtc);

public sealed class RecentWorkspaceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _storePath;
    private readonly object _gate = new();

    public RecentWorkspaceStore(string? directory = null)
    {
        var baseDirectory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WeChatVoiceToolkit");
        _storePath = Path.Combine(baseDirectory, "recent-workspaces.json");
    }

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
            var entries = Load().Where(entry => !string.Equals(entry.WorkspacePath, Path.GetFullPath(workspacePath), StringComparison.OrdinalIgnoreCase)).ToList();
            entries.Insert(0, new RecentWorkspaceEntry(
                Path.GetFullPath(workspacePath),
                workspace.Workspace.WorkspaceId,
                workspace.DataSet.DataSetId,
                workspace.DataSet.AccountId,
                DateTimeOffset.UtcNow));
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
}
