using WeChatVoice.Desktop.Infrastructure;
using WeChatVoice.Workflows.Composition;

namespace WeChatVoice.Desktop;

/// <summary>
/// Desktop composition root. The UI depends only on the shared workflows and
/// Desktop infrastructure; it never touches SQLite, process memory, or a Key
/// Broker implementation directly.
/// </summary>
public sealed class DesktopServices : IAsyncDisposable
{
    /// <summary>
    /// Public for tests: hosts and tests can supply a composition root built
    /// with fake workflows and app-data paths under a temp directory.
    /// </summary>
    public DesktopServices(
        WorkflowCompositionRoot workflows,
        DesktopLog log,
        RecentWorkspaceStore recentWorkspaces,
        OperationCoordinator? operationCoordinator = null,
        Func<Action, Task>? invokeOnUi = null,
        IWeixinDataSourceDiscovery? dataSourceDiscovery = null,
        IWeixinProcessProbe? weixinProcessProbe = null,
        SnapshotOutputDirectoryFactory? snapshotOutputDirectories = null,
        IDesktopFolderPicker? folderPicker = null)
    {
        Workflows = workflows;
        Log = log;
        RecentWorkspaces = recentWorkspaces;
        OperationCoordinator = operationCoordinator ?? new OperationCoordinator();
        InvokeOnUi = invokeOnUi;
        Project = new ExportProjectSession();
        FolderPicker = folderPicker ?? new DesktopFolderPicker();
        DataSourceDiscovery = dataSourceDiscovery ?? new WeixinDataSourceDiscovery(recentWorkspaces);
        WeixinProcessProbe = weixinProcessProbe ?? new WeixinProcessProbe();
        SnapshotOutputDirectories = snapshotOutputDirectories
            ?? new SnapshotOutputDirectoryFactory(recentWorkspaces.StorageDirectory);
        WorkspaceOutputDirectories = new WorkspaceOutputDirectoryFactory(recentWorkspaces.StorageDirectory);
    }

    public static DesktopServices Create(bool allowDevelopmentBroker = false, string? appDataDirectory = null)
    {
        var log = new DesktopLog(appDataDirectory is null ? null : Path.Combine(appDataDirectory, "logs"));
        var recentWorkspaces = new RecentWorkspaceStore(appDataDirectory);
        // Pages pass their own UI-backed confirmation port per run; the
        // composition root only requires a port for construction.
        var workflows = new WorkflowCompositionRoot(SilentAccountConfirmation.Instance, allowDevelopmentBroker, appDataDirectory: appDataDirectory);
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

    public OperationCoordinator OperationCoordinator { get; }

    /// <summary>
    /// Optional host-provided UI dispatcher. Production leaves this null and
    /// pages use Avalonia.UIThread; headless hosts inject an awaitable adapter.
    /// </summary>
    public Func<Action, Task>? InvokeOnUi { get; }

    public ExportProjectSession Project { get; }

    public IDesktopFolderPicker FolderPicker { get; }

    public IWeixinDataSourceDiscovery DataSourceDiscovery { get; }

    public IWeixinProcessProbe WeixinProcessProbe { get; }

    public SnapshotOutputDirectoryFactory SnapshotOutputDirectories { get; }

    public WorkspaceOutputDirectoryFactory WorkspaceOutputDirectories { get; }

    public async ValueTask DisposeAsync()
    {
        await Workflows.DisposeAsync().ConfigureAwait(false);
    }
}
