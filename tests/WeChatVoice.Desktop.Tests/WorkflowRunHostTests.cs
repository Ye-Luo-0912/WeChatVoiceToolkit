using WeChatVoice.Core.Models;
using WeChatVoice.Desktop.Infrastructure;
using WeChatVoice.Workflows.Workflows;

namespace WeChatVoice.Desktop.Tests;

/// <summary>
/// WorkflowRunHost drives the shared Workflow State Machine; a direct marshaler
/// replaces the UI thread so these are deterministic unit tests. Failures are
/// surfaced through State and LastError, not by rethrowing to the caller.
/// </summary>
public sealed class WorkflowRunHostTests
{
    private static WorkflowRunHost CreateHost() => new(marshal: action => action());

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
        var first = new WorkflowRunHost(marshal: action => action(), coordinator: coordinator);
        var second = new WorkflowRunHost(marshal: action => action(), coordinator: coordinator);
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        var firstRun = first.RunAsync((_, cancellationToken) => Task.Run(() =>
        {
            started.Set();
            release.Wait(cancellationToken);
        }, cancellationToken));
        started.Wait();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            second.RunAsync((_, _) => Task.CompletedTask));

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
}
