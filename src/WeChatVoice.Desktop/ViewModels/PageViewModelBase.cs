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

    protected PageViewModelBase(DesktopServices services, Func<Action, Task>? invokeOnUi = null)
    {
        Services = services;
        _marshal = invokeOnUi
            ?? services.InvokeOnUi
            ?? (action => Dispatcher.UIThread.InvokeAsync(action).GetTask());
        RunHost = new WorkflowRunHost(invokeOnUi: _marshal, log: services.Log, coordinator: services.OperationCoordinator);
        services.OperationCoordinator.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName is nameof(OperationCoordinator.IsBusy) or null)
            {
                _ = ApplyOnUiThreadAsync(() => OnPropertyChanged(nameof(CanStartOperation)));
            }
        };
        RunHost.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName is nameof(WorkflowRunHost.IsRunning) or nameof(WorkflowRunHost.State))
            {
                _ = ApplyOnUiThreadAsync(() => OnPropertyChanged(nameof(CanStartOperation)));
            }
        };
        services.Project.PropertyChanged += (_, eventArgs) =>
        _ = ApplyOnUiThreadAsync(() =>
            {
                OnPropertyChanged(nameof(CanNavigate));
                OnPropertyChanged(nameof(NavigationHint));
                OnProjectPropertyChanged(eventArgs.PropertyName);
            });
    }

    protected DesktopServices Services { get; }

    protected WorkflowCompositionRoot Workflows => Services.Workflows;

    /// <summary>Dispatches page state changes to the Avalonia UI thread.</summary>
    protected Task ApplyOnUiThreadAsync(Action action) => _marshal(action);

    protected DialogAccountConfirmation CreateAccountConfirmation()
        => new(_marshal);

    /// <summary>Allows pages to refresh derived session summaries on the UI thread.</summary>
    protected virtual void OnProjectPropertyChanged(string? propertyName)
    {
    }

    /// <summary>Owns the current run's state machine, progress, and cancellation.</summary>
    public WorkflowRunHost RunHost { get; }

    public virtual bool CanNavigate => true;

    /// <summary>High-cost page commands are disabled while any app operation is active.</summary>
    public bool CanStartOperation => !Services.OperationCoordinator.IsBusy && !RunHost.IsRunning;

    public virtual string? NavigationHint => null;

    public abstract string Title { get; }
}
