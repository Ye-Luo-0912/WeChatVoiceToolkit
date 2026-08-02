using Avalonia;
using Avalonia.Headless;
using WeChatVoice.Core.Models;
using WeChatVoice.Desktop.Infrastructure;
using WeChatVoice.Desktop.ViewModels;
using WeChatVoice.Desktop.Views;
using WeChatVoice.Workflows.Composition;
using WeChatVoice.Workflows.Workflows;

namespace WeChatVoice.Desktop.Tests;

public sealed class AvaloniaHeadlessSmokeTests
{
    [Fact]
    public void Main_window_and_all_page_views_load_under_headless_platform()
    {
        var app = AppBuilder.Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions())
            .SetupWithoutStarting();
        var services = DesktopServices.Create(appDataDirectory: Path.Combine(Path.GetTempPath(), "WeChatVoiceToolkit.Headless", Guid.NewGuid().ToString("N")));
        var window = new MainWindow { DataContext = new MainWindowViewModel(services) };

        Assert.NotNull(window);
        Assert.NotNull(new EnvironmentView());
        Assert.NotNull(new SourceSnapshotView());
        Assert.NotNull(new MaterializationView());
        Assert.NotNull(new ContactView());
        Assert.NotNull(new ScanView());
        Assert.NotNull(new ExportView());
        Assert.NotNull(new HistoryDiagnosticsView());
    }

    [Fact]
    public async Task Folder_picker_requires_a_real_attached_storage_owner()
    {
        var picker = new DesktopFolderPicker();
        await Assert.ThrowsAsync<InvalidOperationException>(() => picker.PickFolderAsync("test"));
    }

    [Fact]
    public async Task Fake_backend_runs_the_guided_flow_to_the_second_contact()
    {
        using var temporary = new TestTemporaryDirectory();
        var source = temporary.CreateDirectory("source");
        var snapshot = temporary.GetPath("snapshot");
        var export = temporary.GetPath("export");
        var fakeSnapshot = new FakeSnapshotWorkflow();
        var fakeMaterialization = new FakeMaterializationWorkflow();
        var fakeContacts = new FakeContactWorkflow();
        var fakeScan = new FakeScanWorkflow();
        var fakeExport = new FakeExportWorkflow();
        var root = new WorkflowCompositionRoot(
            new TestDoubles.SilentConfirmation(),
            environmentAssessment: new FakeEnvironmentWorkflow(),
            snapshot: fakeSnapshot,
            materialization: fakeMaterialization,
            contactDiscovery: fakeContacts,
            voiceScan: fakeScan,
            voiceExport: fakeExport);
        var services = new DesktopServices(
            root,
            new DesktopLog(temporary.Root),
            new RecentWorkspaceStore(temporary.Root),
            invokeOnUi: DirectInvokeAsync);
        var main = new MainWindowViewModel(services);

        var sourcePage = Assert.IsType<SourceSnapshotViewModel>(main.Pages[1]);
        var materialization = Assert.IsType<MaterializationViewModel>(main.Pages[2]);
        Assert.False(sourcePage.CanNavigate);
        Assert.False(materialization.CanNavigate);

        var environment = Assert.IsType<EnvironmentViewModel>(main.Pages[0]);
        await environment.AssessCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WorkflowState.Completed, environment.RunHost.State);

        Assert.True(sourcePage.CanNavigate);
        sourcePage.SourceDirectory = source;
        sourcePage.OutputDirectory = snapshot;
        await sourcePage.CreateSnapshotCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WorkflowState.Completed, sourcePage.RunHost.State);
        Assert.Equal(false, fakeSnapshot.LastRequest?.AllowLiveSource);

        Assert.True(materialization.CanNavigate);
        materialization.OutputDirectory = temporary.GetPath("materialized");
        materialization.WorkspaceOutputPath = temporary.GetPath("materialized.workspace.json");
        var materializationRun = materialization.MaterializeCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => materialization.IsConfirmDialogOpen);
        materialization.ConfirmAccountCommand.Execute(null);
        await materializationRun.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WorkflowState.Completed, materialization.RunHost.State);

        var contact = Assert.IsType<ContactViewModel>(main.Pages[3]);
        await contact.LoadContactsCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(5));
        contact.SelectedContact = contact.Contacts[1];
        Assert.Equal("contact-b", services.Project.SelectedContact?.ContactId);

        var scan = Assert.IsType<ScanViewModel>(main.Pages[4]);
        await scan.ScanCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(VoiceDirection.Incoming, fakeScan.LastRequest?.Direction);
        Assert.Equal("contact-b", services.Project.SelectionPlan?.ContactId);

        var exportPage = Assert.IsType<ExportViewModel>(main.Pages[5]);
        exportPage.OutputDirectory = export;
        await exportPage.ExportCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WorkflowState.Completed, exportPage.RunHost.State);
        Assert.Equal("wxid_b", fakeExport.LastRequest?.ContactUsername);
        Assert.Equal(VoiceDirection.Incoming, fakeExport.LastRequest?.Direction);
        Assert.Equal(services.Project.SelectionPlan?.ResultSetFingerprint, fakeExport.LastRequest?.ExpectedResultSetFingerprint);
        main.SelectedPage = exportPage;
        Assert.Same(exportPage, main.SelectedPage);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "The fake workflow did not reach the expected UI state.");
    }

    private static Task DirectInvokeAsync(Action action)
    {
        action();
        return Task.CompletedTask;
    }

    private sealed class TestTemporaryDirectory : IDisposable
    {
        public TestTemporaryDirectory()
        {
            Root = Path.Combine(Path.GetTempPath(), "WeChatVoiceToolkit.DesktopHeadless", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string GetPath(string relativePath) => Path.Combine(Root, relativePath);

        public string CreateDirectory(string relativePath)
        {
            var path = GetPath(relativePath);
            Directory.CreateDirectory(path);
            return path;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
