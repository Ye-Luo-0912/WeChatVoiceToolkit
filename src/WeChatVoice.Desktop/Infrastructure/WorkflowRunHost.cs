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

    /// <summary>True when the current stage reports a bounded percent (0..100).</summary>
    public bool HasPercent => PercentComplete is not null;

    /// <summary>ProgressBar-safe percent; 0 when no bounded percent is available.</summary>
    public double PercentCompleteOrZero => PercentComplete ?? 0;

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

        session.TransitionHandler = (_, _) => QueueStateChanged(session);
        session.StateMachine.Transitioned += session.TransitionHandler;
        LastError = null;
        LastErrorCode = null;
        LastTransportErrorCode = null;
        LastAppError = null;
        Log?.Info($"run {version} starting");

        if (!session.StateMachine.TryStart())
        {
            DetachSession(session);
            session.Dispose();

            operationLease?.Dispose();
            _runGate.Release();
            throw new InvalidOperationException("The workflow session could not be started.");
        }

        QueueStateChanged(session);
        var context = new WorkflowContext(
            confirmation,
            // Progress<T> dispatches through SynchronizationContext and can
            // invoke the callback after the operation task has completed.
            // Use a synchronous adapter here so the session queue contains
            // every progress item before ExecuteAsync drains it.
            new SessionProgress(progress => QueueProgress(session, progress)),
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
            await session.DrainUiWorkAsync().ConfigureAwait(false);
            if (applyOnUiThread is not null)
            {
                await InvokeIfCurrentAsync(session, () => applyOnUiThread(result)).ConfigureAwait(false);
            }

            await InvokeIfCurrentAsync(session, () => CompleteRun(session, exception: null)).ConfigureAwait(false);
            Log?.Info($"run {session.Version} completed");
        }
        catch (OperationCanceledException) when (session.Cancellation.IsCancellationRequested)
        {
            await session.DrainUiWorkAsync().ConfigureAwait(false);
            await InvokeIfCurrentAsync(session, () => CompleteRun(session, new OperationCanceledException(session.Cancellation.Token))).ConfigureAwait(false);
            Log?.Info($"run {session.Version} cancelled");
        }
        catch (Exception exception)
        {
            await session.DrainUiWorkAsync().ConfigureAwait(false);
            await InvokeIfCurrentAsync(session, () => CompleteRun(session, exception)).ConfigureAwait(false);
        }
        finally
        {
            // CompleteRun normally detaches on the UI dispatcher after the
            // terminal state has been applied. This fallback also handles a
            // dispatcher shutdown or a failed final callback without leaving
            // a stale session able to accept late progress.
            DetachSession(session);
            session.Dispose();
            operationLease?.Dispose();
            _runGate.Release();
        }
    }

    private void QueueProgress(WorkflowRunSession session, OperationProgress progress)
        => session.QueueUiWork(() => InvokeIfCurrentAsync(session, () => OnProgress(session, progress)));

    private void QueueStateChanged(WorkflowRunSession session)
        => session.QueueUiWork(() => InvokeIfCurrentAsync(session, () => OnStateChanged(session)));

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
        OnPropertyChanged(nameof(HasPercent));
        OnPropertyChanged(nameof(PercentCompleteOrZero));
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
        DetachSession(session);
    }

    private void SetTypedError(Exception exception)
    {
        switch (exception)
        {
            case AppFailureException app:
                LastErrorCode = app.Code;
                LastAppError = ErrorCatalog.Get(app.Code);
                LastError = $"[{app.Code}] {GetUserFacingError(app.Code, LastAppError.SuggestedAction)}";
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

    private static string GetUserFacingError(ErrorCode code, string fallbackAction)
        => code switch
        {
            ErrorCode.GroupChatNotSupported => "当前联系人包含群聊语音，首版不支持群聊导出，请返回联系人页选择一对一联系人。",
            ErrorCode.ContactNotFound => "未找到所选联系人，请重新加载联系人列表并选择联系人。",
            ErrorCode.WorkspaceInvalid => "工作区校验失败，请重新物料化数据后再扫描。",
            ErrorCode.UnsupportedSchema => "数据库结构不受当前版本支持，请重新物料化或检查微信版本。",
            ErrorCode.UnsupportedWeixinVersion => "当前运行的 Weixin 版本不受支持。请在环境检测页查看实际版本；当前 Profile 仅支持 4.1.11.55。若已有 Workspace，请从“继续上次工作”直接复用。",
            ErrorCode.DurationResolverUnavailable => "试听需要 SILK 解码器。请先进入“语音扫描”，在解码器设置中选择可用的解码器，然后重新扫描或返回本页重试。",
            ErrorCode.InvalidRequest => "当前操作无法继续：请先完成联系人和语音扫描；已导出数据会自动复用，无需重新选择目录。",
            _ => fallbackAction,
        };

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
        else if (State == WorkflowState.Running
            && string.Equals(StageMessage, "等待账号确认…", StringComparison.Ordinal))
        {
            StageMessage = "已确认账号，正在继续…";
        }
        else if (State == WorkflowState.Failed)
        {
            StageMessage = "操作失败";
        }
        else if (State == WorkflowState.Cancelled)
        {
            StageMessage = "操作已取消";
        }
    }

    private void DetachSession(WorkflowRunSession session)
    {
        lock (_sessionGate)
        {
            if (ReferenceEquals(_activeSession, session))
            {
                _activeSession = null;
            }
        }

        if (session.TransitionHandler is { } handler)
        {
            session.StateMachine.Transitioned -= handler;
            session.TransitionHandler = null;
        }
    }

    private static async Task InvokeOnAvaloniaUiAsync(Action action)
        => await Dispatcher.UIThread.InvokeAsync(action);

    private readonly struct Unit
    {
        public static Unit Value { get; } = new();
    }

    private sealed class SessionProgress : IProgress<OperationProgress>
    {
        private readonly Action<OperationProgress> _report;

        public SessionProgress(Action<OperationProgress> report)
            => _report = report ?? throw new ArgumentNullException(nameof(report));

        public void Report(OperationProgress value) => _report(value);
    }
}
