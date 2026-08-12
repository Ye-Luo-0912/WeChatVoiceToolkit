using System.Text.Json;
using System.Text.Json.Serialization;
using WeChatVoice.Core.Models;

namespace WeChatVoice.Infrastructure.Storage;

/// <summary>
/// Records user-selected export and dataset roots so inventory can account for
/// their size without treating the Recent index as an ownership database.
/// Registration is metadata only; registered user assets are never deleted by
/// the default cleanup policy.
/// </summary>
public sealed class ManagedStoragePathRegistry
{
    private const string FileName = "managed-storage-roots.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _path;
    private readonly object _gate = new();

    public ManagedStoragePathRegistry(string appDataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataRoot);
        var root = Path.GetFullPath(appDataRoot);
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, FileName);
    }

    public IReadOnlyList<ManagedStoragePath> Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path)) return [];
            try
            {
                return JsonSerializer.Deserialize<List<ManagedStoragePath>>(File.ReadAllText(_path), JsonOptions) ?? [];
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                return [];
            }
        }
    }

    public void Register(string path, StorageAssetKind kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (kind is not (StorageAssetKind.UserAsset or StorageAssetKind.DerivedUserAsset))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), "Only user export and derived dataset roots may be registered.");
        }

        var full = Path.GetFullPath(path);
        lock (_gate)
        {
            var entries = Load().Where(item => !string.Equals(item.Path, full, StringComparison.OrdinalIgnoreCase)).ToList();
            entries.Insert(0, new ManagedStoragePath(full, kind, DateTimeOffset.UtcNow));
            Save(entries.Take(100).ToArray());
        }
    }

    public void Remove(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);
        lock (_gate)
        {
            Save(Load().Where(item => !string.Equals(item.Path, full, StringComparison.OrdinalIgnoreCase)).ToArray());
        }
    }

    private void Save(IReadOnlyList<ManagedStoragePath> entries)
    {
        try
        {
            var temp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temp, JsonSerializer.Serialize(entries, JsonOptions));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Registry is an optimization for inventory, never a workflow
            // failure or a reason to block an export/dataset operation.
        }
    }
}

public sealed record ManagedStoragePath(
    string Path,
    StorageAssetKind Kind,
    DateTimeOffset RegisteredUtc);
