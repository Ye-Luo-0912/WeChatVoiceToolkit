using WeChatVoice.Core.Models;
using WeChatVoice.Core.Ports;

namespace WeChatVoice.Workflows.Workflows;

/// <summary>
/// Per-run host contract handed to a shared workflow. Hosts create one context
/// per run, bind to <see cref="StateMachine"/> before starting, and observe
/// progress through <see cref="Progress"/>. Interactive steps (account
/// confirmation) go through the injected <see cref="IAccountConfirmation"/>
/// port so the same workflow works on the CLI and in a UI dialog.
/// </summary>
public sealed class WorkflowContext
{
    private static readonly IProgress<OperationProgress> Noop = new NoopProgress();

    public WorkflowContext(IAccountConfirmation accountConfirmation, IProgress<OperationProgress>? progress = null)
    {
        AccountConfirmation = accountConfirmation ?? throw new ArgumentNullException(nameof(accountConfirmation));
        Progress = progress ?? Noop;
    }

    public WorkflowStateMachine StateMachine { get; } = new();

    public IAccountConfirmation AccountConfirmation { get; }

    public IProgress<OperationProgress> Progress { get; }

    /// <summary>
    /// Emits a running progress event with the given well-known stage. Status
    /// reflects the state machine so hosts see WaitingForUser during account
    /// confirmation without extra coordination.
    /// </summary>
    public void Report(OperationPhase phase, string stageId, string? message = null, double? percentComplete = null)
    {
        var status = StateMachine.State == WorkflowState.AwaitingUser
            ? OperationStatus.WaitingForUser
            : OperationStatus.Running;
        Progress.Report(new OperationProgress(phase, status, new OperationStage(stageId, message, percentComplete)));
    }

    private sealed class NoopProgress : IProgress<OperationProgress>
    {
        public void Report(OperationProgress value)
        {
        }
    }
}
