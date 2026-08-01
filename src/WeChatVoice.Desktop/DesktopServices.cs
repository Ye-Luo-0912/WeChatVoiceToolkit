using WeChatVoice.Desktop.Infrastructure;
using WeChatVoice.Workflows.Composition;

namespace WeChatVoice.Desktop;

/// <summary>
/// Desktop composition root. The UI depends only on the shared workflows and
/// Desktop infrastructure; it never touches SQLite, process memory, or a Key
/// Broker implementation directly.
/// </summary>
public sealed class DesktopServices
{
    /// <summary>
    /// Public for tests: hosts and tests can supply a composition root built
    /// with fake workflows and app-data paths under a temp directory.
    /// </summary>
    public DesktopServices(
        WorkflowCompositionRoot workflows,
        DesktopLog log,
        RecentWorkspaceStore recentWorkspaces)
    {
        Workflows = workflows;
        Log = log;
        RecentWorkspaces = recentWorkspaces;
    }

    public static DesktopServices Create(bool allowDevelopmentBroker = false, string? appDataDirectory = null)
    {
        var log = new DesktopLog(appDataDirectory is null ? null : Path.Combine(appDataDirectory, "logs"));
        var recentWorkspaces = new RecentWorkspaceStore(appDataDirectory);
        // Pages pass their own UI-backed confirmation port per run; the
        // composition root only requires a port for construction.
        var workflows = new WorkflowCompositionRoot(SilentAccountConfirmation.Instance, allowDevelopmentBroker);
        return new DesktopServices(workflows, log, recentWorkspaces);
    }

    private sealed class SilentAccountConfirmation : Core.Ports.IAccountConfirmation
    {
        internal static SilentAccountConfirmation Instance { get; } = new();

        public Task<Core.Models.AccountConfirmation> ConfirmAsync(
            Core.Models.AccountIdentityReport report,
            CancellationToken cancellationToken)
            => Task.FromResult(new Core.Models.AccountConfirmation(false, null));
    }

    public WorkflowCompositionRoot Workflows { get; }

    public DesktopLog Log { get; }

    public RecentWorkspaceStore RecentWorkspaces { get; }
}
