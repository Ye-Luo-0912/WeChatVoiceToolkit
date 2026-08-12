using System.Text.Json;
using System.Text.Json.Serialization;

namespace WeChatVoice.Desktop.Infrastructure;

/// <summary>
/// Stores only local Seed-VC tool paths and verified derived-artifact paths.
/// It is keyed by the Dataset Build fingerprint so a changed dataset never
/// silently reuses a previous model or preparation directory.
/// </summary>
public sealed record SeedVcSettings(
    string DatasetBuildFingerprint,
    string? SeedVcRoot = null,
    string? PythonPath = null,
    string? AnchorDirectory = null,
    string? PrepDirectory = null,
    string? LastRunDirectory = null,
    string? CheckpointPath = null,
    DateTimeOffset? LastUsedUtc = null);

public sealed class SeedVcSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _path;
    private readonly object _gate = new();

    public SeedVcSettingsStore(string storageDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageDirectory);
        Directory.CreateDirectory(storageDirectory);
        _path = Path.Combine(Path.GetFullPath(storageDirectory), "seedvc-settings.json");
    }

    public SeedVcSettings? Load(string? datasetBuildFingerprint)
    {
        if (string.IsNullOrWhiteSpace(datasetBuildFingerprint)) return null;
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path)) return null;
                var entries = JsonSerializer.Deserialize<List<SeedVcSettings>>(File.ReadAllText(_path), JsonOptions) ?? [];
                return entries.FirstOrDefault(entry => string.Equals(entry.DatasetBuildFingerprint, datasetBuildFingerprint, StringComparison.OrdinalIgnoreCase));
            }
            catch (IOException) { return null; }
            catch (JsonException) { return null; }
        }
    }

    public void Save(SeedVcSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.DatasetBuildFingerprint);
        lock (_gate)
        {
            var entries = ReadUnsafe();
            entries.RemoveAll(entry => string.Equals(entry.DatasetBuildFingerprint, settings.DatasetBuildFingerprint, StringComparison.OrdinalIgnoreCase));
            entries.Insert(0, settings with { LastUsedUtc = DateTimeOffset.UtcNow });
            while (entries.Count > 20) entries.RemoveAt(entries.Count - 1);
            var temp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temp, JsonSerializer.Serialize(entries, JsonOptions));
                File.Move(temp, _path, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
            }
        }
    }

    private List<SeedVcSettings> ReadUnsafe()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<List<SeedVcSettings>>(File.ReadAllText(_path), JsonOptions) ?? []
                : [];
        }
        catch (IOException) { return []; }
        catch (JsonException) { return []; }
    }
}
