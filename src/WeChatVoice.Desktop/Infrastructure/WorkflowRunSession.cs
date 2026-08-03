using WeChatVoice.Workflows.Workflows;

namespace WeChatVoice.Desktop.Infrastructure;

/// <summary>
/// Owns all mutable state for one Desktop workflow invocation. The gate is
/// shared by the host's sessions, so creating a second session cannot race a
/// still-active operation; the version is used to discard delayed UI work
/// from an older session.
/// </summary>
public sealed class WorkflowRunSession : IDisposable
{
    public WorkflowRunSession(long version, SemaphoreSlim runGate)
    {
        if (version <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(version));
        }

        Version = version;
        RunGate = runGate ?? throw new ArgumentNullException(nameof(runGate));
        StateMachine = new WorkflowStateMachine();
        Cancellation = new CancellationTokenSource();
    }

    public WorkflowStateMachine StateMachine { get; }

    public CancellationTokenSource Cancellation { get; }

    public long Version { get; }

    public SemaphoreSlim RunGate { get; }

    private readonly object _uiWorkGate = new();
    private Task _pendingUiWork = Task.CompletedTask;

    internal EventHandler<WorkflowStateTransition>? TransitionHandler { get; set; }

    internal void QueueUiWork(Func<Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        lock (_uiWorkGate)
        {
            _pendingUiWork = _pendingUiWork
                .ContinueWith(
                    static (_, state) => ((Func<Task>)state!).Invoke(),
                    work,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default)
                .Unwrap();
        }
    }

    internal Task DrainUiWorkAsync()
    {
        lock (_uiWorkGate)
        {
            return _pendingUiWork;
        }
    }

    public void Dispose() => Cancellation.Dispose();
}
