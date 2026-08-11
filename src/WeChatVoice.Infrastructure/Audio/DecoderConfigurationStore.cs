using System.Text.Json;
using WeChatVoice.Infrastructure.Serialization;

namespace WeChatVoice.Infrastructure.Audio;

/// <summary>
/// User-facing, persistent SILK decoder configuration stored only under
/// LocalApplicationData. It lets a normal user point the product at a
/// reviewed decoder without touching environment variables; the environment
/// variables remain the advanced/development path. The store never contains
/// raw database data, key material, or memory contents.
/// </summary>
public sealed class DecoderConfigurationStore
{
    private readonly string _storePath;
    private readonly object _gate = new();

    public DecoderConfigurationStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        StorageDirectory = Path.GetFullPath(directory);
        _storePath = Path.Combine(StorageDirectory, "decoder-config.json");
    }

    /// <summary>Application-local metadata root for the decoder configuration.</summary>
    public string StorageDirectory { get; }

    /// <summary>
    /// Reads the configured reviewed decoder worker executable path, or null
    /// when the user has not configured one. A path that no longer exists on
    /// disk is still returned so the UI can explain why it is unreachable.
    /// </summary>
    public string? LoadWorkerPath()
    {
        lock (_gate)
        {
            if (!File.Exists(_storePath))
            {
                return null;
            }

            try
            {
                var json = File.ReadAllText(_storePath);
                var config = JsonSerializer.Deserialize<DecoderConfigFile>(json, InfrastructureJson.Compact);
                return string.IsNullOrWhiteSpace(config?.WorkerPath) ? null : config.WorkerPath;
            }
            catch (JsonException)
            {
                return null;
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Persists the reviewed decoder worker executable path. Passing null or
    /// whitespace clears the configured path.
    /// </summary>
    public void SetWorkerPath(string? workerPath)
    {
        lock (_gate)
        {
            var normalized = string.IsNullOrWhiteSpace(workerPath) ? null : Path.GetFullPath(workerPath);
            try
            {
                Directory.CreateDirectory(StorageDirectory);
                File.WriteAllText(_storePath, JsonSerializer.Serialize(new DecoderConfigFile(normalized), InfrastructureJson.Compact));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed record DecoderConfigFile(string? WorkerPath);
}