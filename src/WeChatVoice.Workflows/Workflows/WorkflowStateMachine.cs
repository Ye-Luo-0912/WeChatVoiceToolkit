namespace WeChatVoice.Workflows.Workflows;

/// <summary>
/// Explicit lifecycle of one workflow run. Hosts (CLI, Desktop) bind to a run's
/// machine before starting it so they can render state, offer cancellation, and
/// react to terminal states without parsing progress text. Transitions that are
/// not valid for the current state return false and are ignored.
/// </summary>
public enum WorkflowState
{
    Idle,
    Running,
    AwaitingUser,
    Cancelling,
    Cancelled,
    Completed,
    Failed,
}

public sealed record WorkflowStateTransition(WorkflowState From, WorkflowState To);

public sealed class WorkflowStateMachine
{
    private readonly object _gate = new();
    private WorkflowState _state = WorkflowState.Idle;

    /// <summary>Raised on the transition thread after every accepted transition.</summary>
    public event EventHandler<WorkflowStateTransition>? Transitioned;

    public WorkflowState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>Begins a fresh run. Allowed from Idle and every terminal state.</summary>
    public bool TryStart()
    {
        if (!TryTransition(WorkflowState.Running, WorkflowState.Idle, WorkflowState.Completed, WorkflowState.Failed, WorkflowState.Cancelled))
        {
            return false;
        }

        return true;
    }

    /// <summary>The run pauses because the host must confirm the account identity.</summary>
    public bool TryEnterAwaitingUser() => TryTransition(WorkflowState.AwaitingUser, WorkflowState.Running);

    /// <summary>The user answered the account confirmation and the run resumes.</summary>
    public bool TryResumeFromUser() => TryTransition(WorkflowState.Running, WorkflowState.AwaitingUser);

    /// <summary>The user requested cancellation; the run observes it and stops.</summary>
    public bool TryRequestCancellation() => TryTransition(WorkflowState.Cancelling, WorkflowState.Running, WorkflowState.AwaitingUser);

    public bool TryComplete() => TryTransition(WorkflowState.Completed, WorkflowState.Running);

    public bool TryFail() => TryTransition(WorkflowState.Failed, WorkflowState.Running, WorkflowState.Cancelling);

    /// <summary>
    /// Marks the run cancelled. The normal host path goes Running ->
    /// Cancelling (TryRequestCancellation) -> Cancelled; a workflow that
    /// observes external cancellation (token cancelled outside the host) moves
    /// Running/AwaitingUser directly to Cancelled.
    /// </summary>
    public bool TryCancel() => TryTransition(WorkflowState.Cancelled, WorkflowState.Cancelling, WorkflowState.Running, WorkflowState.AwaitingUser);

    private bool TryTransition(WorkflowState to, params WorkflowState[] allowedFrom)
    {
        lock (_gate)
        {
            if (!allowedFrom.Contains(_state))
            {
                return false;
            }

            var from = _state;
            _state = to;
            Transitioned?.Invoke(this, new WorkflowStateTransition(from, to));
            return true;
        }
    }
}
