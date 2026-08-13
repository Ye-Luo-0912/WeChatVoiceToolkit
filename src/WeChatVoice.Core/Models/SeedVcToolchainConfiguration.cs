namespace WeChatVoice.Core.Models;

/// <summary>
/// User-owned Seed-VC toolchain settings. Only locations and an OpenSSH host
/// alias are stored; credentials and private keys remain under the operating
/// system's SSH configuration.
/// </summary>
public sealed record SeedVcToolchainConfiguration(
    string? SeedVcRoot = null,
    string? PythonPath = null,
    string? FfmpegPath = null,
    string? ConfigPath = null,
    string? LinuxHost = null,
    string? LinuxUser = null,
    int? LinuxPort = null,
    string? LinuxSeedVcRoot = null,
    string? LinuxPythonPath = null,
    string? LinuxFfmpegPath = null,
    string Format = "wechatvoice-seedvc-toolchain-v1")
{
    public const string CurrentFormat = "wechatvoice-seedvc-toolchain-v1";
}

/// <summary>Effective settings after CLI, environment and global config resolution.</summary>
public sealed record SeedVcToolchainResolution(
    string? SeedVcRoot,
    string PythonPath,
    string? FfmpegPath,
    string? ConfigPath,
    string? LinuxHost,
    string? LinuxUser,
    int? LinuxPort,
    string? LinuxSeedVcRoot,
    string? LinuxPythonPath,
    string? LinuxFfmpegPath,
    string GlobalConfigPath)
{
    public bool HasLinuxTarget => !string.IsNullOrWhiteSpace(LinuxHost);
}

/// <summary>Read-only connectivity check for the configured Linux Seed-VC host.</summary>
public sealed record SeedVcRemoteProbeReport(
    bool IsReady,
    bool IsReachable,
    string? Host,
    string? Platform,
    string? PythonVersion,
    string? TorchVersion,
    bool? CudaAvailable,
    string? GpuName,
    bool ModelAssetsReady,
    string? FfmpegVersion,
    bool SeedVcFound,
    string? SeedVcRoot,
    IReadOnlyList<string> Issues,
    DateTimeOffset CheckedAtUtc);
