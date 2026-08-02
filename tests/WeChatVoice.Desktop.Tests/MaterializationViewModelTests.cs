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
