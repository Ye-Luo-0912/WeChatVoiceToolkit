using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WeChatVoice.Core.Errors;
using WeChatVoice.Core.Models;
using WeChatVoice.Workflows.Workflows;

namespace WeChatVoice.Desktop.Infrastructure;

/// <summary>
/// Owns the Desktop lifecycle for one workflow at a time. A run session holds
/// the exact StateMachine passed to WorkflowContext, its cancellation source,
/// and a monotonically increasing version. Results and progress are applied
/// on the UI dispatcher only when their session is still current.
/// </summary>
public sealed partial class WorkflowRunHost : ObservableObject
{
    private readonly Func<Action, Task> _invokeOnUi;
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly OperationCoordinator? _coordinator;
    private readonly object _sessionGate = new();
    private WorkflowRunSession? _activeSession;
    private Func<Task>? _retry;
    private long _runVersion;

    public WorkflowRunHost(Func<Action, Task>? invokeOnUi = null, DesktopLog? log = null, OperationCoordinator? coordinator = null)
    {
        _invokeOnUi = invokeOnUi ?? InvokeOnAvaloniaUiAsync;
        Log = log;
        _coordinator = coordinator;
    }

    /// <summary>The state machine for the current (or most recent) session.</summary>
    public WorkflowStateMachine StateMachine { get; private set; } = new();

    public DesktopLog? Log { get; }

    [ObservableProperty]
    private WorkflowState _state;

    [ObservableProperty]
    private string? _stageId;

    [ObservableProperty]
    private string? _stageMessage;

    [ObservableProperty]
    private double? _percentComplete;

    /// <summary>
    /// Safe presentation text kept for existing bindings. UI branching uses
    /// the typed code properties below and never parses this text.
    /// </summary>
    [ObservableProperty]
    private string? _lastError;

    [ObservableProperty]
    private ErrorCode? _lastErrorCode;

    [ObservableProperty]
    private BrokerTransportErrorCode? _lastTransportErrorCode;

    [ObservableProperty]
    private AppError? _lastAppError;

    public bool IsRunning => State is WorkflowState.Running or WorkflowState.AwaitingUser or WorkflowState.Cancelling;

    public bool CanCancel => State is WorkflowState.Running or WorkflowState.AwaitingUser;

    public bool CanRetry => State is WorkflowState.Failed or WorkflowState.Cancelled or WorkflowState.Completed;

    public bool IsAwaitingUser => State == WorkflowState.AwaitingUser;

    /// <summary>
    /// Convenience overload for operations without account confirmation.
    /// </summary>
    public Task RunAsync(Func<WorkflowContext, CancellationToken, Task> operation)
        => RunAsync(() => new DialogAccountConfirmation(), operation);

    public Task RunAsync(
        DialogAccountConfirmation confirmation,
        Func<WorkflowContext, CancellationToken, Task> operation)
        => RunAsync(() => confirmation, operation);

    /// <summary>
    /// Runs a non-result operation with a fresh confirmation instance on every
    /// retry. The factory overload is important for interactive workflows:
    /// retrying must create a new confirmation session, not reuse a completed
    /// dialog object.
    /// </summary>
    public Task RunAsync(
        Func<DialogAccountConfirmation> confirmationFactory,
        Func<WorkflowContext, CancellationToken, Task> operation)
    {
        ArgumentNullException.ThrowIfNull(confirmationFactory);
        ArgumentNullException.ThrowIfNull(operation);
        return StartRun(
            confirmationFactory,
            async (context, cancellationToken) =>
            {
                await operation(context, cancellationToken).ConfigureAwait(false);
                return Unit.Value;
            },
            applyOnUiThread: null);
    }

    public Task RunAsync<TResult>(
        Func<WorkflowContext, CancellationToken, Task<TResult>> operation,
        Action<TResult> applyOnUiThread)
        => RunAsync(() => new DialogAccountConfirmation(), operation, applyOnUiThread);

    public Task RunAsync<TResult>(
        DialogAccountConfirmation confirmation,
        Func<WorkflowContext, CancellationToken, Task<TResult>> operation,
        Action<TResult> applyOnUiThread)
        => RunAsync(() => confirmation, operation, applyOnUiThread);

    /// <summary>
    /// Executes the workflow away from the UI thread. The result callback is
    /// dispatched and awaited before the returned task completes, so page
    /// properties and collections are never changed by a worker thread.
    /// </summary>
    public Task RunAsync<TResult>(
        Func<DialogAccountConfirmation> confirmationFactory,
        Func<WorkflowContext, CancellationToken, Task<TResult>> operation,
        Action<TResult> applyOnUiThread)
    {
        ArgumentNullException.ThrowIfNull(confirmationFactory);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(applyOnUiThread);
        return StartRun(confirmationFactory, operation, applyOnUiThread);
    }

    [RelayCommand]
    private void Cancel()
    {
        WorkflowRunSession? session;
        lock (_sessionGate)
        {
            session = _activeSession;
        }

        if (session is null)
        {
            return;
        }

        var state = session.StateMachine.State;
        if (state is not (WorkflowState.Running or WorkflowState.AwaitingUser))
        {
            return;
        }

        if (session.StateMachine.TryRequestCancellation())
        {
            session.Cancellation.Cancel();
        }
    }

    [RelayCommand]
    private void Retry()
    {
        if (_retry is null || IsRunning)
        {
            return;
        }

        _ = _retry();
    }

    private Task StartRun<TResult>(
        Func<DialogAccountConfirmation> confirmationFactory,
        Func<WorkflowContext, CancellationToken, Task<TResult>> operation,
        Action<TResult>? applyOnUiThread)
    {
        if (!_runGate.Wait(0))
        {
            LastErrorCode = ErrorCode.OperationBusy;
            LastAppError = ErrorCatalog.Get(ErrorCode.OperationBusy);
            LastError = $"[{ErrorCode.OperationBusy}] {LastAppError.SuggestedAction}";
            return Task.CompletedTask;
        }

        IDisposable? operationLease = null;
        if (_coordinator is not null && !_coordinator.TryAcquire(out operationLease))
        {
            _runGate.Release();
            LastErrorCode = ErrorCode.OperationBusy;
            LastAppError = ErrorCatalog.Get(ErrorCode.OperationBusy);
            LastError = $"[{ErrorCode.OperationBusy}] {LastAppError.SuggestedAction}";
            return Task.CompletedTask;
        }

        var version = Interlocked.Increment(ref _runVersion);
        var session = new WorkflowRunSession(version, _runGate);
        DialogAccountConfirmation confirmation;
        try
        {
            confirmation = confirmationFactory()
                ?? throw new InvalidOperationException("The account confirmation factory returned null.");
        }
        catch
        {
            operationLease?.Dispose();
            _runGate.Release();
            throw;
        }

        lock (_sessionGate)
        {
            _activeSession = session;
            StateMachine = session.StateMachine;
            _retry = () => StartRun(confirmationFactory, operation, applyOnUiThread);
        }

        session.StateMachine.Transitioned += (_, _) => QueueStateChanged(session);
        LastError = null;
        LastErrorCode = null;
        LastTransportErrorCode = null;
        LastAppError = null;
        Log?.Info($"run {version} starting");

        if (!session.StateMachine.TryStart())
        {
            session.Dispose();
            lock (_sessionGate)
            {
                if (ReferenceEquals(_activeSession, session))
                {
                    _activeSession = null;
                }
            }

            operationLease?.Dispose();
            _runGate.Release();
            throw new InvalidOperationException("The workflow session could not be started.");
        }

        QueueStateChanged(session);
        var context = new WorkflowContext(
            confirmation,
            new Progress<OperationProgress>(progress => QueueProgress(session, progress)),
            session.StateMachine);
        return ExecuteAsync(session, context, operation, applyOnUiThread, operationLease);
    }

    private async Task ExecuteAsync<TResult>(
        WorkflowRunSession session,
        WorkflowContext context,
        Func<WorkflowContext, CancellationToken, Task<TResult>> operation,
        Action<TResult>? applyOnUiThread,
        IDisposable? operationLease)
    {
        try
        {
            var result = await Task.Run(
                () => operation(context, session.Cancellation.Token),
                session.Cancellation.Token).ConfigureAwait(false);
            if (applyOnUiThread is not null)
            {
                await InvokeIfCurrentAsync(session, () => applyOnUiThread(result)).ConfigureAwait(false);
            }

            await InvokeIfCurrentAsync(session, () => CompleteRun(session, exception: null)).ConfigureAwait(false);
            Log?.Info($"run {session.Version} completed");
        }
        catch (OperationCanceledException) when (session.Cancellation.IsCancellationRequested)
        {
            await InvokeIfCurrentAsync(session, () => CompleteRun(session, new OperationCanceledException(session.Cancellation.Token))).ConfigureAwait(false);
            Log?.Info($"run {session.Version} cancelled");
        }
        catch (Exception exception)
        {
            await InvokeIfCurrentAsync(session, () => CompleteRun(session, exception)).ConfigureAwait(false);
        }
        finally
        {
            session.Dispose();
            operationLease?.Dispose();
            _runGate.Release();
        }
    }

    private void QueueProgress(WorkflowRunSession session, OperationProgress progress)
        => _ = InvokeIfCurrentAsync(session, () => OnProgress(session, progress));

    private void QueueStateChanged(WorkflowRunSession session)
        => _ = InvokeIfCurrentAsync(session, () => OnStateChanged(session));

    private async Task InvokeIfCurrentAsync(WorkflowRunSession session, Action action)
    {
        await _invokeOnUi(() =>
        {
            if (IsCurrent(session))
            {
                action();
            }
        }).ConfigureAwait(false);
    }

    private bool IsCurrent(WorkflowRunSession session)
    {
        lock (_sessionGate)
        {
            return ReferenceEquals(_activeSession, session)
                && session.Version == Volatile.Read(ref _runVersion);
        }
    }

    private void OnProgress(WorkflowRunSession session, OperationProgress progress)
    {
        StageId = progress.Stage.Id;
        StageMessage = progress.Stage.Message;
        PercentComplete = progress.Stage.PercentComplete;
        Log?.Stage(progress.Phase, progress.Stage.Id, progress.Stage.PercentComplete);
    }

    private void CompleteRun(WorkflowRunSession session, Exception? exception)
    {
        if (exception is null)
        {
            if (session.StateMachine.State == WorkflowState.Running)
            {
                session.StateMachine.TryComplete();
            }
        }
        else if (exception is OperationCanceledException)
        {
            if (session.StateMachine.State is WorkflowState.Running or WorkflowState.AwaitingUser or WorkflowState.Cancelling)
            {
                session.StateMachine.TryCancel();
            }
        }
        else
        {
            SetTypedError(exception);
            if (session.StateMachine.State is WorkflowState.Running or WorkflowState.Cancelling)
            {
                session.StateMachine.TryFail();
            }
        }

        OnStateChanged(session);
    }

    private void SetTypedError(Exception exception)
    {
        switch (exception)
        {
            case AppFailureException app:
                LastErrorCode = app.Code;
                LastAppError = ErrorCatalog.Get(app.Code);
                LastError = $"[{app.Code}] {LastAppError.SuggestedAction}";
                Log?.ErrorCode(app.Code);
                break;
            case BrokerTransportException transport:
                LastTransportErrorCode = transport.Code;
                LastError = $"[{transport.Code}] retry";
                Log?.ErrorCode(transport.Code);
                break;
            default:
                // Unknown boundary failures are deliberately normalized. Do
                // not expose exception type, message, path, or SQLite text.
                const ErrorCode code = ErrorCode.WorkflowFailed;
                LastErrorCode = code;
                LastAppError = ErrorCatalog.Get(code);
                LastError = $"[{code}] {LastAppError.SuggestedAction}";
                Log?.ErrorCode(code);
                break;
        }
    }

    private void OnStateChanged(WorkflowRunSession session)
    {
        State = session.StateMachine.State;
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(IsAwaitingUser));
        if (State == WorkflowState.AwaitingUser)
        {
            StageMessage = "等待账号确认…";
        }
    }

    private static async Task InvokeOnAvaloniaUiAsync(Action action)
        => await Dispatcher.UIThread.InvokeAsync(action);

    private readonly struct Unit
    {
        public static Unit Value { get; } = new();
    }
}
