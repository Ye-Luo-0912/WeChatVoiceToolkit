using WeChatVoice.Core.Errors;
using WeChatVoice.Desktop.Infrastructure;
using WeChatVoice.Desktop.ViewModels;
using WeChatVoice.Workflows.Composition;
using WeChatVoice.Workflows.Workflows;

namespace WeChatVoice.Desktop.Tests;

/// <summary>
/// Materialization page tests: account-confirmation dialog flow and UAC
/// rejection surfacing. No real Broker or Weixin is involved; the WorkflowRunHost
/// surfaces failures through State and LastError rather than rethrowing.
/// </summary>
public sealed class MaterializationViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WeChatVoiceToolkit.DesktopTests", Guid.NewGuid().ToString("N"));
    private readonly FakeMaterializationWorkflow _workflow = new();

    public MaterializationViewModelTests()
    {
        var root = new WorkflowCompositionRoot(
            new TestDoubles.SilentConfirmation(),
            materialization: _workflow);
        Services = new DesktopServices(root, new DesktopLog(_root), new RecentWorkspaceStore(_root));
        Services.Project.EnvironmentAssessment = new FakeEnvironmentWorkflow().Result;
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

    private DesktopServices Services { get; }

    private MaterializationViewModel CreateViewModel() => new(Services, DirectInvokeAsync);

    private static Task DirectInvokeAsync(Action action)
    {
        action();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Materialize_prompts_for_account_then_completes()
    {
        var viewModel = CreateViewModel();
        viewModel.SnapshotDirectory = "C:\\snapshots\\s";
        viewModel.OutputDirectory = "C:\\output";

        var run = viewModel.MaterializeCommand.ExecuteAsync(null);
        await SpinWaitUntilAsync(() => viewModel.IsConfirmDialogOpen);

        // The workflow blocks on the confirmation port; the page shows the dialog.
        Assert.Equal("wxid_owner", viewModel.PendingAccountCandidate);

        viewModel.ConfirmAccountCommand.Execute(null);
        await run;

        Assert.False(viewModel.IsConfirmDialogOpen);
        Assert.Equal(WorkflowState.Completed, viewModel.RunHost.State);
        Assert.Contains("wxid_owner", viewModel.IdentitySummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Declining_account_fails_with_account_confirmation_required()
    {
        var viewModel = CreateViewModel();
        viewModel.SnapshotDirectory = "C:\\snapshots\\s";
        viewModel.OutputDirectory = "C:\\output";

        var run = viewModel.MaterializeCommand.ExecuteAsync(null);
        await SpinWaitUntilAsync(() => viewModel.IsConfirmDialogOpen);

        viewModel.DeclineAccountCommand.Execute(null);
        await run;

        Assert.Equal(WorkflowState.Failed, viewModel.RunHost.State);
        Assert.Equal(ErrorCode.AccountConfirmationRequired, viewModel.RunHost.LastErrorCode);
    }

    [Fact]
    public async Task Uac_rejection_is_surfaced_as_a_typed_error()
    {
        _workflow.Throw = new AppFailureException(ErrorCode.UacElevationRejected, "Elevation was declined.");
        var viewModel = CreateViewModel();
        viewModel.SnapshotDirectory = "C:\\snapshots\\s";
        viewModel.OutputDirectory = "C:\\output";

        await viewModel.MaterializeCommand.ExecuteAsync(null);

        Assert.Equal(WorkflowState.Failed, viewModel.RunHost.State);
        Assert.True(viewModel.IsUacRejected);
        Assert.Equal(ErrorCode.UacElevationRejected, viewModel.RunHost.LastErrorCode);
    }

    [Fact]
    public async Task Cancel_stops_a_running_materialization()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _workflow.OnRun = _ => gate.TrySetResult();
        var viewModel = CreateViewModel();
        viewModel.SnapshotDirectory = "C:\\snapshots\\s";
        viewModel.OutputDirectory = "C:\\output";

        var run = viewModel.MaterializeCommand.ExecuteAsync(null);
        await gate.Task;
        Assert.True(viewModel.RunHost.IsRunning);

        viewModel.RunHost.CancelCommand.Execute(null);
        await run;
        Assert.Equal(WorkflowState.Cancelled, viewModel.RunHost.State);
    }

    [Fact]
    public async Task Materialization_requires_environment_trust_preflight()
    {
        Services.Project.EnvironmentAssessment = null;
        var viewModel = CreateViewModel();
        viewModel.SnapshotDirectory = "C:\\snapshots\\s";
        viewModel.OutputDirectory = "C:\\output";

        await viewModel.MaterializeCommand.ExecuteAsync(null);

        Assert.Equal(WorkflowState.Failed, viewModel.RunHost.State);
        Assert.Equal(ErrorCode.InvalidRequest, viewModel.RunHost.LastErrorCode);
        Assert.False(viewModel.IsConfirmDialogOpen);
    }

    [Fact]
    public async Task Recoverable_failure_exposes_desktop_recovery_without_redecrypting()
    {
        var output = Directory.CreateDirectory(Path.Combine(_root, "recoverable-output")).FullName;
        _workflow.Throw = new AppFailureException(ErrorCode.MaterializationInvalid, "materialization failed");
        _workflow.OnRun = _ => Directory.CreateDirectory(output);
        var workspaceWorkflow = new FakeWorkspaceWorkflow();
        var root = new WorkflowCompositionRoot(
            new TestDoubles.SilentConfirmation(),
            materialization: _workflow,
            workspace: workspaceWorkflow);
        var services = new DesktopServices(root, new DesktopLog(_root), new RecentWorkspaceStore(_root), invokeOnUi: DirectInvokeAsync);
        services.Project.EnvironmentAssessment = new FakeEnvironmentWorkflow().Result;
        var viewModel = new MaterializationViewModel(services, DirectInvokeAsync)
        {
            SnapshotDirectory = "C:\\snapshots\\s",
            OutputDirectory = output,
        };

        await viewModel.MaterializeCommand.ExecuteAsync(null);

        Assert.True(viewModel.CanRecoverMaterialization);
        Assert.Contains("不重新解密", viewModel.RecoverySummary, StringComparison.Ordinal);

        await viewModel.RecoverMaterializationCommand.ExecuteAsync(null);

        Assert.False(viewModel.CanRecoverMaterialization);
        Assert.Equal(Path.GetFullPath(output), workspaceWorkflow.LastRecoveryRequest?.OutputDirectory);
        Assert.Same(workspaceWorkflow.RecoveryResult, services.Project.Workspace);
    }

    [Fact]
    public void Materialization_snapshot_path_tracks_the_project_session()
    {
        var viewModel = CreateViewModel();
        Services.Project.SnapshotDirectory = "C:\\snapshots\\first";
        Assert.Equal(Services.Project.SnapshotDirectory, viewModel.SnapshotDirectory);

        Services.Project.SnapshotDirectory = "C:\\snapshots\\second";
        Assert.Equal(Services.Project.SnapshotDirectory, viewModel.SnapshotDirectory);
    }

    [Fact]
    public void Materialization_allocates_output_and_workspace_paths_automatically()
    {
        var viewModel = CreateViewModel();

        Services.Project.SnapshotDirectory = Path.Combine(_root, "snapshot");

        Assert.False(string.IsNullOrWhiteSpace(viewModel.OutputDirectory));
        Assert.False(string.IsNullOrWhiteSpace(viewModel.WorkspaceOutputPath));
        Assert.EndsWith(".workspace.json", viewModel.WorkspaceOutputPath, StringComparison.OrdinalIgnoreCase);
        Assert.True(viewModel.CanStartMaterialization);
    }

    internal static async Task SpinWaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException("Timed out waiting for the UI condition.");
            }

            await Task.Delay(10);
        }
    }
}
