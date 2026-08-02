using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WeChatVoice.Workflows.Workflows;

namespace WeChatVoice.Desktop.ViewModels;

/// <summary>
/// Capability check page. Only the live-validated Weixin 4.1.11.55 build is
/// supported; the page reports the detected process version and gates the
/// rest of the flows on a matching key-acquisition profile.
/// </summary>
public sealed partial class EnvironmentViewModel : PageViewModelBase
{
    public EnvironmentViewModel(DesktopServices services)
        : base(services)
    {
    }

    public override string Title => "环境检测";

    [ObservableProperty]
    private string? _workspacePath;

    [ObservableProperty]
    private bool _isWindows;

    [ObservableProperty]
    private bool _isWeixinRunning;

    [ObservableProperty]
    private string? _detectedVersion;

    [ObservableProperty]
    private bool _isSupportedVersion;

    [ObservableProperty]
    private string _supportedVersionText = "仅支持 Weixin 4.1.11.55";

    [ObservableProperty]
    private string _detectionSummary = "尚未检测";

    [ObservableProperty]
    private string _capabilitySummary = string.Empty;

    [ObservableProperty]
    private bool _workerInstalled;

    [ObservableProperty]
    private bool _brokerInstalled;

    [ObservableProperty]
    private bool _brokerAvailable;

    [ObservableProperty]
    private string _brokerTrustSummary = "未验证";

    [ObservableProperty]
    private string _workerBundleTrustSummary = "未验证";

    [ObservableProperty]
    private string _installDirectorySecuritySummary = "未验证";

    [ObservableProperty]
    private string? _workspaceSummary;

    [RelayCommand]
    private Task AssessAsync()
    {
        var workspacePath = WorkspacePath;
        return RunHost.RunAsync(
        async (context, cancellationToken) => await Workflows.EnvironmentAssessment.RunAsync(
            new EnvironmentAssessmentRequest(workspacePath),
            context,
            cancellationToken).ConfigureAwait(false),
        result =>
        {
            if (result.Workspace is not null)
            {
                Services.Project.Workspace = result.Workspace;
                Services.Project.WorkspacePath = WorkspacePath;
            }
            IsWindows = result.IsWindows;
            IsWeixinRunning = result.RunningWeChatProcesses.Count > 0;
            IsSupportedVersion = result.MatchingKeyAcquisitionProfiles.Count > 0;
            DetectedVersion = IsSupportedVersion
                ? "4.1.11.55"
                : result.RunningWeChatProcesses.FirstOrDefault()?.ProcessName;
            DetectionSummary = IsSupportedVersion
                ? $"已检测到受支持的 Weixin {DetectedVersion}（身份证据匹配）"
                : IsWeixinRunning
                    ? $"检测到 Weixin {DetectedVersion}，但该版本不受支持"
                    : "未检测到运行中的 Weixin 进程";
            WorkerInstalled = result.WorkerInstalled;
            BrokerInstalled = result.BrokerInstalled;
            BrokerAvailable = result.BrokerAcquireAndMaterializeAvailable;
            BrokerTrustSummary = result.BrokerTrustResult is { } brokerTrust
                ? (brokerTrust.Verified ? "Broker 信任校验通过" : $"Broker 信任校验失败：{brokerTrust.NonSensitiveReason}")
                : "Broker 信任校验未执行";
            WorkerBundleTrustSummary = result.WorkerBundleTrustResult is { } workerTrust
                ? (workerTrust.Verified ? "Worker Bundle 校验通过" : $"Worker Bundle 校验失败：{workerTrust.NonSensitiveReason}")
                : "Worker Bundle 校验未执行";
            InstallDirectorySecuritySummary = result.InstallDirectorySecurity is { } security
                ? (security.Protected ? "安装目录不可由当前用户写入" : "安装目录安全校验失败")
                : "安装目录安全校验未执行";
            CapabilitySummary = BrokerAvailable
                ? "Key Broker / SQLCipher Worker 就绪，可以执行物料化"
                : !WorkerInstalled || !BrokerInstalled
                    ? "缺少 WeChatVoice.KeyBroker.exe 或 WeChatVoice.SqlCipherWorker.exe"
                    : "缺少匹配的密钥提取 Profile（请确认 Weixin 4.1.11.55 正在运行）";
            WorkspaceSummary = result.Workspace is null
                ? null
                : $"Workspace {result.Workspace.Workspace.WorkspaceId} 校验通过；账号：{(result.Workspace.DataSet.AccountId ?? "（未绑定）")}；匹配适配器：{string.Join("、", result.MatchingAdapters)}";
        });
    }
}
