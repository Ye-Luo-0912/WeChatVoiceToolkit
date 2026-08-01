using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WeChatVoice.Core.Models;
using WeChatVoice.Workflows.Workflows;

namespace WeChatVoice.Desktop.Infrastructure;

/// <summary>
/// Owns one workflow run on a page: the explicit state machine, cancellation,
/// progress marshaling to the UI thread, and retry. Large files and hashing
/// always execute on the thread pool; only state/progress updates are posted
/// to the UI thread. All reported messages are non-sensitive (stages, error
/// codes); contact data never flows through this host.
/// </summary>
public sealed partial class WorkflowRunHost : ObservableObject
{
    private readonly Action<Action> _marshal;
    private CancellationTokenSource? _cts;
    private DialogAccountConfirmation? _lastConfirmation;
    private Func<WorkflowContext, CancellationToken, Task>? _lastAction;
    private int _runVersion;

    public WorkflowRunHost(Action<Action>? marshal = null, DesktopLog? log = null)
    {
        _marshal = marshal ?? (action => Dispatcher.UIThread.Post(action));
        Log = log;
        StateMachine.Transitioned += (_, _) => _marshal(OnStateChanged);
    }

    public WorkflowStateMachine StateMachine { get; } = new();

    public DesktopLog? Log { get; }

    [ObservableProperty]
    private WorkflowState _state;

    [ObservableProperty]
    private string? _stageId;

    [ObservableProperty]
    private string? _stageMessage;

    [ObservableProperty]
    private double? _percentComplete;

    [ObservableProperty]
    private string? _lastError;

    public bool IsRunning => State is WorkflowState.Running or WorkflowState.AwaitingUser or WorkflowState.Cancelling;

    public bool CanCancel => State is WorkflowState.Running or WorkflowState.AwaitingUser;

    public bool CanRetry => State is WorkflowState.Failed or WorkflowState.Cancelled or WorkflowState.Completed;

    public bool IsAwaitingUser => State == WorkflowState.AwaitingUser;

    /// <summary>
    /// Convenience overload for pages whose workflows do not prompt for
    /// account confirmation (environment, snapshot, contact, scan, export).
    /// </summary>
    public Task RunAsync(Func<WorkflowContext, CancellationToken, Task> action)
        => RunAsync(new DialogAccountConfirmation(), action);

    /// <summary>
    /// Runs one workflow invocation. The caller supplies the account
    /// confirmation port (page VMs pass a UI-backed
    /// <see cref="DialogAccountConfirmation"/>) and the run; cancellation
    /// requests flow through a fresh token per run. File and hash work runs on
    /// the thread pool; state and progress updates are marshaled to the UI
    /// thread.
    /// </summary>
    public Task RunAsync(DialogAccountConfirmation confirmation, Func<WorkflowContext, CancellationToken, Task> action)
    {
        ArgumentNullException.ThrowIfNull(confirmation);
        ArgumentNullException.ThrowIfNull(action);
        if (IsRunning)
        {
            throw new InvalidOperationException("A workflow run is already active.");
        }

        var version = ++_runVersion;
        var cts = new CancellationTokenSource();
        _cts = cts;
        _lastConfirmation = confirmation;
        _lastAction = action;
        LastError = null;
        Log?.Info($"run {version} starting");
        StateMachine.TryStart();

        var context = new WorkflowContext(
            confirmation,
            new Progress<OperationProgress>(progress => _marshal(() => OnProgress(progress))));

        return Task.Run(async () =>
        {
            try
            {
                await action(context, cts.Token).ConfigureAwait(false);
                _marshal(() => OnRunCompleted(null));
                Log?.Info($"run {version} completed");
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                _marshal(() => OnRunCompleted(new OperationCanceledException(cts.Token)));
                Log?.Info($"run {version} cancelled");
            }
            catch (Exception exception)
            {
                _marshal(() => OnRunCompleted(exception));
            }
            finally
            {
                cts.Dispose();
            }
        });
    }

    /// <summary>Requests cancellation of the active run (button binding).</summary>
    [RelayCommand]
    private void Cancel()
    {
        if (CanCancel)
        {
            StateMachine.TryRequestCancellation();
            _cts?.Cancel();
        }
    }

    /// <summary>Re-runs the last action with a fresh run (button binding).</summary>
    [RelayCommand]
    private void Retry()
    {
        if (_lastAction is null || _lastConfirmation is null || IsRunning)
        {
            return;
        }

        RunAsync(_lastConfirmation, _lastAction);
    }

    private void OnProgress(OperationProgress progress)
    {
        StageId = progress.Stage.Id;
        StageMessage = progress.Stage.Message;
        PercentComplete = progress.Stage.PercentComplete;
        Log?.Stage(progress.Phase, progress.Stage.Id, progress.Stage.PercentComplete);
    }

    private void OnRunCompleted(Exception? exception)
    {
        if (exception is null)
        {
            StateMachine.TryComplete();
        }
        else if (exception is OperationCanceledException)
        {
            StateMachine.TryCancel();
        }
        else
        {
            StateMachine.TryFail();
            LastError = ToDisplayError(exception);
            Log?.Error(LastError);
        }

        _cts = null;
    }

    private void OnStateChanged()
    {
        State = StateMachine.State;
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(IsAwaitingUser));
        if (State == WorkflowState.AwaitingUser)
        {
            StageMessage = "等待账号确认…";
        }
    }

    /// <summary>
    /// Maps typed failures to display text. Codes come from the error catalog;
    /// exception messages are used only when non-sensitive (the lower layers
    /// already constrain them to non-sensitive text).
    /// </summary>
    private static string ToDisplayError(Exception exception) => exception switch
    {
        Core.Errors.AppFailureException app => $"[{app.Code}] {app.Message}",
        Core.Errors.BrokerTransportException transport => $"[{transport.Code}] {transport.Message}",
        _ => $"{exception.GetType().Name}: {exception.Message}",
    };
}
