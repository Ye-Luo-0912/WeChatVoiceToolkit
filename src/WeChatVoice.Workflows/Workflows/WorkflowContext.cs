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

    public WorkflowContext(
        IAccountConfirmation accountConfirmation,
        IProgress<OperationProgress>? progress = null,
        WorkflowStateMachine? stateMachine = null)
    {
        AccountConfirmation = accountConfirmation ?? throw new ArgumentNullException(nameof(accountConfirmation));
        Progress = progress ?? Noop;
        StateMachine = stateMachine ?? new WorkflowStateMachine();
    }

    /// <summary>
    /// The state machine supplied by the host for this run. A context created
    /// directly by the CLI or a test gets its own machine for compatibility;
    /// Desktop passes the active run machine so there is only one observable
    /// state source.
    /// </summary>
    public WorkflowStateMachine StateMachine { get; }

    public IAccountConfirmation AccountConfirmation { get; }

    public IProgress<OperationProgress> Progress { get; }

    /// <summary>
    /// Starts a workflow when it owns the context directly. A Desktop host
    /// starts the shared session before dispatching the operation, in which
    /// case this is an idempotent validation of the already-running state.
    /// </summary>
    public bool TryStart()
    {
        var state = StateMachine.State;
        return state == WorkflowState.Running
            || (state == WorkflowState.Idle && StateMachine.TryStart());
    }

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
