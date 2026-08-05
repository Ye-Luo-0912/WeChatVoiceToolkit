using WeChatVoice.Desktop.Infrastructure;
using WeChatVoice.Desktop.ViewModels;
using WeChatVoice.Workflows.Broker;
using WeChatVoice.Workflows.Composition;
using WeChatVoice.Workflows.Workflows;

namespace WeChatVoice.Desktop.Tests;

public sealed class EnvironmentViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "WeChatVoiceToolkit.DesktopEnvironmentTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Shows_broker_failure_instead_of_claiming_missing_profile()
    {
        var environment = new FakeEnvironmentWorkflow
        {
            Result = CreateResult(
                available: false,
                brokerTrust: BrokerTrustResult.Deny("broker-publisher-mismatch"),
                workerTrust: WorkerBundleTrustResult.Ok(),
                security: new InstallDirectorySecurityResult(
                    Protected: false,
                    UserWritable: false,
                    NonSensitiveReason: "broker-publisher-mismatch",
                    SecurityState: InstallSecurityState.NotEvaluated)),
        };
        await using var services = CreateServices(environment);
        var viewModel = new EnvironmentViewModel(services);

        await viewModel.AssessCommand.ExecuteAsync(null);

        Assert.Equal("Broker 信任失败：broker-publisher-mismatch", viewModel.CapabilitySummary);
        Assert.Equal("安装目录保护检查未执行（Broker 信任链提前失败）", viewModel.InstallDirectorySecuritySummary);
    }

    [Fact]
    public async Task Shows_development_mode_security_message_and_worker_failure()
    {
        var environment = new FakeEnvironmentWorkflow
        {
            Result = CreateResult(
                available: false,
                brokerTrust: BrokerTrustResult.Ok(),
                workerTrust: WorkerBundleTrustResult.Deny("worker-hash-mismatch"),
                security: new InstallDirectorySecurityResult(
                    Protected: false,
                    UserWritable: false,
                    NonSensitiveReason: null,
                    SecurityState: InstallSecurityState.DevelopmentModeNotApplicable)),
        };
        await using var services = CreateServices(environment);
        var viewModel = new EnvironmentViewModel(services);

        await viewModel.AssessCommand.ExecuteAsync(null);

        Assert.Equal("Worker Bundle 信任失败：worker-hash-mismatch", viewModel.CapabilitySummary);
        Assert.Equal("开发信任模式：正式安装目录保护检查不适用", viewModel.InstallDirectorySecuritySummary);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private DesktopServices CreateServices(FakeEnvironmentWorkflow environment)
    {
        Directory.CreateDirectory(_root);
        var root = new WorkflowCompositionRoot(
            new TestDoubles.SilentConfirmation(),
            environmentAssessment: environment);
        return new DesktopServices(
            root,
            new DesktopLog(_root),
            new RecentWorkspaceStore(_root),
            invokeOnUi: DirectInvokeAsync);
    }

    private static EnvironmentAssessmentResult CreateResult(
        bool available,
        BrokerTrustResult brokerTrust,
        WorkerBundleTrustResult workerTrust,
        InstallDirectorySecurityResult security)
        => new(
            IsWindows: true,
            RunningWeChatProcesses: [],
            SupportedProcessNames: [],
            KeyAcquisitionProfiles: [],
            MatchingKeyAcquisitionProfiles: ["profile"],
            RegisteredAdapters: [],
            AdapterMatchEvaluated: false,
            MatchingAdapters: [],
            WorkerInstalled: true,
            BrokerInstalled: true,
            BrokerAcquireAndMaterializeAvailable: available,
            Workspace: null,
            BrokerTrustResult: brokerTrust,
            WorkerBundleTrustResult: workerTrust,
            InstallDirectorySecurity: security);

    private static Task DirectInvokeAsync(Action action)
    {
        action();
        return Task.CompletedTask;
    }
}
