using WeChatVoice.Core.Errors;
using WeChatVoice.Core.Models;
using WeChatVoice.Desktop.Infrastructure;
using WeChatVoice.Workflows.Workflows;

namespace WeChatVoice.Desktop.Tests;

/// <summary>
/// WorkflowRunHost drives the shared Workflow State Machine; a direct UI invoker
/// replaces the UI thread so these are deterministic unit tests. Failures are
/// surfaced through State and LastError, not by rethrowing to the caller.
/// </summary>
public sealed class WorkflowRunHostTests
{
    private static WorkflowRunHost CreateHost() => new(invokeOnUi: DirectInvokeAsync);

    private static Task DirectInvokeAsync(Action action)
    {
        action();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Completed_run_reaches_completed_and_reports_stage()
    {
        var host = CreateHost();
        await host.RunAsync((context, cancellationToken) =>
        {
            context.Report(OperationPhase.VoiceScan, OperationStageIds.QueryingVoices, "正在查询", 42);
            return Task.CompletedTask;
        });

        Assert.Equal(WorkflowState.Completed, host.StateMachine.State);
        Assert.Equal(WorkflowState.Completed, host.State);
        Assert.Equal(OperationStageIds.QueryingVoices, host.StageId);
        Assert.Equal(42, host.PercentComplete);
    }

    [Fact]
    public async Task Late_progress_after_completion_is_discarded()
    {
        WorkflowContext? captured = null;
        var stageAfterCompletion = "querying";
        var host = new WorkflowRunHost(invokeOnUi: DirectInvokeAsync);
        await host.RunAsync((context, _) =>
        {
            captured = context;
            context.Report(OperationPhase.VoiceScan, OperationStageIds.QueryingVoices, stageAfterCompletion, 20);
            return Task.CompletedTask;
        });
        var finalStage = host.StageId;

        captured!.Report(OperationPhase.VoiceExport, OperationStageIds.Exporting, "late progress", 99);
        await Task.Delay(25);

        Assert.Equal(WorkflowState.Completed, host.State);
        Assert.Equal(finalStage, host.StageId);
        Assert.Equal(20, host.PercentComplete);
    }

    [Fact]
    public async Task Cancel_transitions_through_cancelling_to_cancelled()
    {
        var host = CreateHost();
        var run = host.RunAsync((_, cancellationToken) => Task.Delay(TimeSpan.FromMinutes(1), cancellationToken));

        await Task.Yield();
        host.CancelCommand.Execute(null);
        Assert.Equal(WorkflowState.Cancelling, host.State);
        await run;

        Assert.Equal(WorkflowState.Cancelled, host.StateMachine.State);
        Assert.Equal(WorkflowState.Cancelled, host.State);
        Assert.False(host.IsRunning);
        Assert.True(host.CanRetry);
    }

    [Fact]
    public async Task Failed_run_sets_last_error_and_allows_retry()
    {
        var host = CreateHost();
        await host.RunAsync((_, _) => throw new InvalidOperationException("boom"));

        Assert.Equal(WorkflowState.Failed, host.State);
        Assert.Equal(Core.Errors.ErrorCode.WorkflowFailed, host.LastErrorCode);
        Assert.DoesNotContain("boom", host.LastError, StringComparison.Ordinal);
        Assert.True(host.CanRetry);
    }

    [Fact]
    public async Task Retry_runs_the_same_action_again()
    {
        var host = CreateHost();
        var attempts = 0;
        Func<WorkflowContext, CancellationToken, Task> action = (_, _) =>
        {
            attempts++;
            if (attempts == 1)
            {
                throw new InvalidOperationException("first");
            }

            return Task.CompletedTask;
        };

        await host.RunAsync(action);
        Assert.Equal(WorkflowState.Failed, host.State);

        await host.RunAsync(action);
        Assert.Equal(WorkflowState.Completed, host.State);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task Application_coordinator_rejects_a_second_page_operation()
    {
        var coordinator = new OperationCoordinator();
        var first = new WorkflowRunHost(invokeOnUi: DirectInvokeAsync, coordinator: coordinator);
        var second = new WorkflowRunHost(invokeOnUi: DirectInvokeAsync, coordinator: coordinator);
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        var firstRun = first.RunAsync((_, cancellationToken) => Task.Run(() =>
        {
            started.Set();
            release.Wait(cancellationToken);
        }, cancellationToken));
        started.Wait();

        await second.RunAsync((_, _) => Task.CompletedTask);
        Assert.Equal(ErrorCode.OperationBusy, second.LastErrorCode);

        release.Set();
        await firstRun;
        Assert.Equal(WorkflowState.Completed, first.State);
    }

    [Fact]
    public async Task Account_confirmation_dialog_flow()
    {
        var host = CreateHost();
        var confirmation = new DialogAccountConfirmation();
        var requested = new TaskCompletionSource<AccountIdentityReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        confirmation.ConfirmationRequested += (_, report) => requested.TrySetResult(report);

        var run = host.RunAsync(confirmation, async (context, cancellationToken) =>
        {
            var result = await context.AccountConfirmation.ConfirmAsync(
                new AccountIdentityReport("wxid_owner", AccountIdentityState.Candidate, null),
                cancellationToken).ConfigureAwait(false);
            if (!result.Confirmed)
            {
                throw new Core.Errors.AppFailureException(Core.Errors.ErrorCode.AccountConfirmationRequired, "declined");
            }
        });

        Assert.Equal("wxid_owner", (await requested.Task).AccountCandidate);
        confirmation.Complete(true, "wxid_owner");
        await run;

        Assert.Equal(WorkflowState.Completed, host.State);
    }

    [Fact]
    public async Task Declined_confirmation_fails_the_run()
    {
        var host = CreateHost();
        var confirmation = new DialogAccountConfirmation();
        var requested = new TaskCompletionSource<AccountIdentityReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        confirmation.ConfirmationRequested += (_, report) => requested.TrySetResult(report);
        var run = host.RunAsync(confirmation, async (context, cancellationToken) =>
        {
            var result = await context.AccountConfirmation.ConfirmAsync(
                new AccountIdentityReport("wxid_owner", AccountIdentityState.Candidate, null),
                cancellationToken).ConfigureAwait(false);
            if (!result.Confirmed)
            {
                throw new Core.Errors.AppFailureException(Core.Errors.ErrorCode.AccountConfirmationRequired, "declined");
            }
        });

        await requested.Task;
        confirmation.Complete(false, null);
        await run;

        Assert.Equal(WorkflowState.Failed, host.State);
        Assert.Contains("AccountConfirmationRequired", host.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Account_confirmation_waits_for_async_ui_dispatch()
    {
        var dispatchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDispatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requested = new TaskCompletionSource<AccountIdentityReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        var confirmation = new DialogAccountConfirmation(async action =>
        {
            dispatchStarted.TrySetResult();
            await releaseDispatch.Task;
            action();
        });
        confirmation.ConfirmationRequested += (_, report) => requested.TrySetResult(report);

        var run = confirmation.ConfirmAsync(
            new AccountIdentityReport("wxid_owner", AccountIdentityState.Candidate, null),
            CancellationToken.None);

        await dispatchStarted.Task;
        Assert.False(requested.Task.IsCompleted);

        releaseDispatch.SetResult();
        Assert.Equal("wxid_owner", (await requested.Task).AccountCandidate);
        confirmation.Complete(true, "wxid_owner");
        var result = await run;

        Assert.True(result.Confirmed);
    }

    [Fact]
    public async Task Account_confirmation_session_can_be_reused_without_reusing_the_first_result()
    {
        var confirmation = new DialogAccountConfirmation(DirectInvokeAsync);
        var requested = new TaskCompletionSource<AccountIdentityReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        confirmation.ConfirmationRequested += (_, report) => requested.TrySetResult(report);

        var firstTask = confirmation.ConfirmAsync(
            new AccountIdentityReport("wxid_first", AccountIdentityState.Candidate, null),
            CancellationToken.None);
        Assert.Equal("wxid_first", (await requested.Task).AccountCandidate);
        confirmation.Complete(true, "wxid_first");
        var first = await firstTask;
        Assert.True(first.Confirmed);

        requested = new TaskCompletionSource<AccountIdentityReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondTask = confirmation.ConfirmAsync(
            new AccountIdentityReport("wxid_second", AccountIdentityState.Candidate, null),
            CancellationToken.None);
        Assert.Equal("wxid_second", (await requested.Task).AccountCandidate);
        confirmation.Complete(false, null);
        var second = await secondTask;

        Assert.False(second.Confirmed);
        Assert.Null(second.ConfirmedAccountId);
    }
}
