using WeChatVoice.Core.Models;

namespace WeChatVoice.Infrastructure.Audio;

/// <summary>
/// Discovers which decoder is configured for duration analysis and reports its
/// product-level status. Discovery order is: the user-facing persistent
/// configuration first, then the advanced environment variable. The report is
/// read-only and non-sensitive; it never runs a decoder payload or touches
/// database data.
/// </summary>
public sealed class DecoderStatusInspector
{
    public const string BundledDecoderFileName = "WeChatVoice.SilkDecoder.exe";
    public const string BundledDecoderSha256 = "afe908fdf8bb5ddc3566caef224a365159a6216e517d8a915db50ce5ecf86d1b";
    public const string WorkerEnvironmentVariable = "WECHATVOICE_SILK_DECODER_WORKER_PATH";
    public const string LegacyEnvironmentVariable = "WECHATVOICE_SILK_DECODER_PATH";

    private readonly DecoderConfigurationStore? _store;
    private readonly Func<string?>? _environment;

    public DecoderStatusInspector(DecoderConfigurationStore? store = null, Func<string?>? environment = null)
    {
        _store = store;
        _environment = environment;
    }

    /// <summary>
    /// Returns the discovered reviewed decoder worker executable path, or null
    /// when none is configured. The persistent user configuration takes
    /// precedence over the environment variable.
    /// </summary>
    public string? DiscoverWorkerPath()
    {
        var configured = _store?.LoadWorkerPath();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var environmentValue = _environment?.Invoke() ?? Environment.GetEnvironmentVariable(WorkerEnvironmentVariable);
        return string.IsNullOrWhiteSpace(environmentValue) ? null : environmentValue;
    }

    /// <summary>Finds the fixed decoder shipped beside the host executable.</summary>
    public static string? DiscoverBundledDecoderPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, BundledDecoderFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
        return string.Equals(hash, BundledDecoderSha256, StringComparison.Ordinal) ? path : null;
    }

    public static bool IsBundledDecoderPath(string path)
        => string.Equals(Path.GetFullPath(path), Path.Combine(AppContext.BaseDirectory, BundledDecoderFileName), StringComparison.OrdinalIgnoreCase)
            && DiscoverBundledDecoderPath() is not null;

    /// <summary>Whether a decoder is actually usable for duration analysis.</summary>
    public bool IsDurationAnalysisAvailable()
        => DiscoverWorkerPath() is { } path && File.Exists(path)
            || DiscoverBundledDecoderPath() is not null;

    /// <summary>
    /// Builds a non-sensitive status report for the configured decoder.
    /// </summary>
    public DecoderStatusReport Report()
    {
        var workerPath = DiscoverWorkerPath();
        if (string.IsNullOrWhiteSpace(workerPath))
        {
            if (DiscoverBundledDecoderPath() is not null)
            {
                return new DecoderStatusReport(
                    DecoderStatus.Available,
                    Protocol: "wechatvoice-bundled-silk-cli-v1",
                    Reason: "已自动启用内置 SILK 解码器。 ");
            }

            return new DecoderStatusReport(
                DecoderStatus.Missing,
                Protocol: ExternalSilkDecoderWorker.ProtocolVersion,
                Reason: "尚未配置 SILK 解码器。可在设置中指定一个已评审的解码器，或使用环境变量。");
        }

        if (!File.Exists(workerPath))
        {
            if (DiscoverBundledDecoderPath() is not null)
            {
                return new DecoderStatusReport(
                    DecoderStatus.Available,
                    Protocol: "wechatvoice-bundled-silk-cli-v1",
                    Reason: "已自动切换到内置 SILK 解码器。 ");
            }

            return new DecoderStatusReport(
                DecoderStatus.FailedSelfTest,
                Protocol: ExternalSilkDecoderWorker.ProtocolVersion,
                Reason: "已配置的解码器可执行文件不存在。");
        }

        if (IsBundledDecoderPath(workerPath))
        {
            return new DecoderStatusReport(
                DecoderStatus.Available,
                Protocol: "wechatvoice-bundled-silk-cli-v1",
                Reason: "已启用内置 SILK 解码器。 ");
        }

        return new DecoderStatusReport(
            DecoderStatus.Available,
            Protocol: ExternalSilkDecoderWorker.ProtocolVersion);
    }
}
