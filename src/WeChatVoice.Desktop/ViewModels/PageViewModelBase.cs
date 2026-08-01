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
    protected PageViewModelBase(DesktopServices services, Action<Action>? marshal = null)
    {
        Services = services;
        RunHost = new WorkflowRunHost(marshal: marshal, log: services.Log);
    }

    protected DesktopServices Services { get; }

    protected WorkflowCompositionRoot Workflows => Services.Workflows;

    /// <summary>Owns the current run's state machine, progress, and cancellation.</summary>
    public WorkflowRunHost RunHost { get; }

    public abstract string Title { get; }
}
