using System.Text.Json;
using WeChatVoice.Core.Models;

namespace WeChatVoice.Infrastructure.SeedVc;

/// <summary>
/// Resolves the shared toolchain configuration used by CLI, Desktop and
/// workflows. Precedence is explicit: request override, environment, global
/// file, then a platform-neutral executable default.
/// </summary>
public sealed class SeedVcToolchainResolver
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public SeedVcToolchainResolver(string? configPath = null)
    {
        GlobalConfigPath = Path.GetFullPath(configPath ?? GetDefaultConfigPath());
    }

    public string GlobalConfigPath { get; }

    public SeedVcToolchainConfiguration Load()
    {
        if (!File.Exists(GlobalConfigPath)) return new SeedVcToolchainConfiguration();
        try
        {
            using var stream = File.OpenRead(GlobalConfigPath);
            return JsonSerializer.Deserialize<SeedVcToolchainConfiguration>(stream, JsonOptions)
                ?? new SeedVcToolchainConfiguration();
        }
        catch (JsonException) { return new SeedVcToolchainConfiguration(); }
        catch (IOException) { return new SeedVcToolchainConfiguration(); }
    }

    public void Save(SeedVcToolchainConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var parent = Path.GetDirectoryName(GlobalConfigPath)
            ?? throw new InvalidOperationException("The global toolchain config has no parent directory.");
        Directory.CreateDirectory(parent);
        var temporary = GlobalConfigPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, configuration with { Format = SeedVcToolchainConfiguration.CurrentFormat }, JsonOptions);
                stream.Flush(true);
            }
            File.Move(temporary, GlobalConfigPath, true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { }
        }
    }

    public SeedVcToolchainResolution Resolve(
        string? seedVcRootOverride = null,
        string? pythonOverride = null,
        string? configOverride = null,
        string? ffmpegOverride = null)
    {
        var saved = Load();
        var linux = OperatingSystem.IsLinux();
        var configuredRoot = First(seedVcRootOverride, Environment.GetEnvironmentVariable("WECHATVOICE_SEEDVC_ROOT"), saved.SeedVcRoot,
            linux ? First(Environment.GetEnvironmentVariable("WECHATVOICE_LINUX_SEEDVC_ROOT"), saved.LinuxSeedVcRoot) : null);
        var configuredPython = First(pythonOverride, Environment.GetEnvironmentVariable("WECHATVOICE_PYTHON"), saved.PythonPath,
            linux ? First(Environment.GetEnvironmentVariable("WECHATVOICE_LINUX_PYTHON"), saved.LinuxPythonPath) : null);
        var configuredFfmpeg = First(ffmpegOverride, Environment.GetEnvironmentVariable("WECHATVOICE_FFMPEG"), saved.FfmpegPath,
            linux ? First(Environment.GetEnvironmentVariable("WECHATVOICE_LINUX_FFMPEG"), saved.LinuxFfmpegPath) : null);
        return new SeedVcToolchainResolution(
            configuredRoot,
            configuredPython ?? (OperatingSystem.IsWindows() ? "python" : "python3"),
            configuredFfmpeg,
            First(configOverride, Environment.GetEnvironmentVariable("WECHATVOICE_SEEDVC_CONFIG"), saved.ConfigPath),
            First(Environment.GetEnvironmentVariable("WECHATVOICE_LINUX_HOST"), saved.LinuxHost),
            First(Environment.GetEnvironmentVariable("WECHATVOICE_LINUX_USER"), saved.LinuxUser),
            ParsePort(First(Environment.GetEnvironmentVariable("WECHATVOICE_LINUX_PORT"), saved.LinuxPort?.ToString(System.Globalization.CultureInfo.InvariantCulture))),
            First(Environment.GetEnvironmentVariable("WECHATVOICE_LINUX_SEEDVC_ROOT"), saved.LinuxSeedVcRoot),
            First(Environment.GetEnvironmentVariable("WECHATVOICE_LINUX_PYTHON"), saved.LinuxPythonPath),
            First(Environment.GetEnvironmentVariable("WECHATVOICE_LINUX_FFMPEG"), saved.LinuxFfmpegPath),
            GlobalConfigPath);
    }

    private static string? First(params string?[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static int? ParsePort(string? value)
        => int.TryParse(value, out var port) && port is >= 1 and <= 65535 ? port : null;

    private static string GetDefaultConfigPath()
    {
        if (OperatingSystem.IsLinux())
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            var configRoot = !string.IsNullOrWhiteSpace(xdg)
                ? xdg
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return Path.Combine(configRoot!, "wechatvoice", "toolchain.json");
        }

        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "WeChatVoiceToolkit", "toolchain.json");
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(string.IsNullOrWhiteSpace(appData) ? AppContext.BaseDirectory : appData, "WeChatVoiceToolkit", "toolchain.json");
    }
}
