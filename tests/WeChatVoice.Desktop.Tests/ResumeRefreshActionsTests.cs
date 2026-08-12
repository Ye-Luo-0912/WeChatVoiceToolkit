using WeChatVoice.Core.Models;
using WeChatVoice.Desktop.Infrastructure;
using WeChatVoice.Desktop.ViewModels;
using WeChatVoice.Workflows.Composition;

namespace WeChatVoice.Desktop.Tests;

/// <summary>
/// Covers the §9 refresh semantics on the Resume home page: the five actions
/// are surfaced as distinct cards and each routes to the page that owns that
/// workflow, so the user never treats "continue" and "re-run everything" the
/// same. The <see cref="IProjectStateWorkflow"/> decision stays authoritative;
/// navigation only moves the user to the owning page.
/// </summary>
public sealed class ResumeRefreshActionsTests
{
    [Fact]
    public async Task Resume_view_model_exposes_the_five_refresh_actions()
    {
        await using var services = await CreateServicesAsync();
        var vm = new ResumeViewModel(services);

        Assert.Equal(5, vm.RefreshActions.Count);
        Assert.Equal(RefreshActionCatalog.ContinueId, vm.RefreshActions[0].Id);
        Assert.Contains(vm.RefreshActions, static action => action.Id == RefreshActionCatalog.RebuildDatasetId);
    }

    [Fact]
    public async Task Refresh_from_source_navigates_to_the_source_snapshot_page()
    {
        await using var services = await CreateServicesAsync();
        var vm = new ResumeViewModel(services);
        Type? requested = null;
        services.Navigation.NavigationRequested += type => requested = type;

        vm.RefreshFromSourceCommand.Execute(null);

        Assert.Equal(typeof(SourceSnapshotViewModel), requested);
    }

    [Fact]
    public async Task Navigate_to_action_routes_each_action_to_its_owning_page()
    {
        await using var services = await CreateServicesAsync();
        var vm = new ResumeViewModel(services);
        var requested = new List<Type>();
        services.Navigation.NavigationRequested += type => requested.Add(type);

        vm.NavigateToActionCommand.Execute(RefreshActionCatalog.RefreshFromSource());
        vm.NavigateToActionCommand.Execute(RefreshActionCatalog.ReScan());
        vm.NavigateToActionCommand.Execute(RefreshActionCatalog.ReAnalyze());
        vm.NavigateToActionCommand.Execute(RefreshActionCatalog.RebuildDataset());

        Assert.Equal(
            [typeof(SourceSnapshotViewModel), typeof(ScanViewModel), typeof(ScanViewModel), typeof(DatasetCurationViewModel)],
            requested);
    }

    [Fact]
    public async Task Navigate_to_action_ignores_a_null_action()
    {
        await using var services = await CreateServicesAsync();
        var vm = new ResumeViewModel(services);
        Type? requested = null;
        services.Navigation.NavigationRequested += type => requested = type;

        vm.NavigateToActionCommand.Execute(null);

        Assert.Null(requested);
    }

    private static async Task<DesktopServices> CreateServicesAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "WeChatVoiceToolkit.ResumeRefresh", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var services = new DesktopServices(
            new WorkflowCompositionRoot(new TestDoubles.SilentConfirmation()),
            new DesktopLog(Path.Combine(root, "logs")),
            new RecentWorkspaceStore(root),
            invokeOnUi: DirectInvokeAsync);
        return services;
    }

    private static Task DirectInvokeAsync(Action action)
    {
        action();
        return Task.CompletedTask;
    }
}
