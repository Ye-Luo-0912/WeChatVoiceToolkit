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

    /// <summary>Whether a decoder is actually usable for duration analysis.</summary>
    public bool IsDurationAnalysisAvailable() => DiscoverWorkerPath() is { } path && File.Exists(path);

    /// <summary>
    /// Builds a non-sensitive status report for the configured decoder.
    /// </summary>
    public DecoderStatusReport Report()
    {
        var workerPath = DiscoverWorkerPath();
        if (string.IsNullOrWhiteSpace(workerPath))
        {
            return new DecoderStatusReport(
                DecoderStatus.Missing,
                Protocol: ExternalSilkDecoderWorker.ProtocolVersion,
                Reason: "尚未配置 SILK 解码器。可在设置中指定一个已评审的解码器，或使用环境变量。");
        }

        if (!File.Exists(workerPath))
        {
            return new DecoderStatusReport(
                DecoderStatus.FailedSelfTest,
                Protocol: ExternalSilkDecoderWorker.ProtocolVersion,
                Reason: "已配置的解码器可执行文件不存在。");
        }

        return new DecoderStatusReport(
            DecoderStatus.Available,
            Protocol: ExternalSilkDecoderWorker.ProtocolVersion);
    }
}
