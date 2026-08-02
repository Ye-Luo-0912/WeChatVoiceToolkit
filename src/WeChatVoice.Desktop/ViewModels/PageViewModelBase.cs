using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using WeChatVoice.Desktop.Infrastructure;
using WeChatVoice.Workflows.Composition;

namespace WeChatVoice.Desktop.ViewModels;

/// <summary>
/// Base for every page. Each page owns a <see cref="WorkflowRunHost"/> so the
/// shared Workflow State Machine is the single source of truth for UI state.
/// </summary>
public abstract partial class PageViewModelBase : ObservableObject
{
    private readonly Func<Action, Task> _marshal;

    protected PageViewModelBase(DesktopServices services, Action<Action>? marshal = null)
    {
        Services = services;
        _marshal = marshal is null
            ? action => Dispatcher.UIThread.InvokeAsync(action).GetTask()
            : action => { marshal(action); return Task.CompletedTask; };
        RunHost = new WorkflowRunHost(invokeOnUi: _marshal, log: services.Log, coordinator: services.OperationCoordinator);
        services.Project.PropertyChanged += (_, _) =>
            _ = ApplyOnUiThreadAsync(() =>
            {
                OnPropertyChanged(nameof(CanNavigate));
                OnPropertyChanged(nameof(NavigationHint));
            });
    }

    protected DesktopServices Services { get; }

    protected WorkflowCompositionRoot Workflows => Services.Workflows;

    /// <summary>Dispatches page state changes to the Avalonia UI thread.</summary>
    protected Task ApplyOnUiThreadAsync(Action action) => _marshal(action);

    protected DialogAccountConfirmation CreateAccountConfirmation()
        => new(action => _ = _marshal(action));

    /// <summary>Owns the current run's state machine, progress, and cancellation.</summary>
    public WorkflowRunHost RunHost { get; }

    public virtual bool CanNavigate => true;

    public virtual string? NavigationHint => null;

    public abstract string Title { get; }
}
