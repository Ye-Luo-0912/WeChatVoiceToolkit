using Avalonia;
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
    public async Task Main_window_and_all_page_views_load_under_headless_platform()
    {
        await HeadlessTestHost.DispatchAsync(async () =>
        {
            await using var services = DesktopServices.Create(appDataDirectory: Path.Combine(Path.GetTempPath(), "WeChatVoiceToolkit.Headless", Guid.NewGuid().ToString("N")));
            var window = new MainWindow { DataContext = new MainWindowViewModel(services) };

            Assert.NotNull(window);
            Assert.NotNull(new EnvironmentView());
            Assert.NotNull(new SourceSnapshotView());
            Assert.NotNull(new MaterializationView());
            Assert.NotNull(new ContactView());
            Assert.NotNull(new ScanView());
            Assert.NotNull(new ExportView());
            Assert.NotNull(new HistoryDiagnosticsView());
        });
    }

    [Fact]
    public async Task Folder_picker_requires_a_real_attached_storage_owner()
    {
        var picker = new DesktopFolderPicker();
        await Assert.ThrowsAsync<InvalidOperationException>(() => picker.PickFolderAsync("test"));
    }

    [Fact]
    public async Task Source_snapshot_view_renders_the_automatic_single_account_summary_under_headless_platform()
    {
        await HeadlessTestHost.DispatchAsync(async () =>
        {
            using var temporary = new TestTemporaryDirectory();
            var source = temporary.CreateDirectory(Path.Combine("wxid_headless_0000000000000008", "db_storage"));
            File.WriteAllBytes(Path.Combine(source, "headless.db"), [1, 2, 3]);
            var root = new WorkflowCompositionRoot(
                new TestDoubles.SilentConfirmation(),
                environmentAssessment: new FakeEnvironmentWorkflow(),
                snapshot: new FakeSnapshotWorkflow());
            await using var services = new DesktopServices(
                root,
                new DesktopLog(temporary.Root),
                new RecentWorkspaceStore(temporary.Root),
                invokeOnUi: DirectInvokeAsync,
                dataSourceDiscovery: new FakeDataSourceDiscovery
                {
                    Result = new WeixinDataSourceDiscoveryResult(
                        [new WeixinDataSourceCandidate(
                            Path.GetDirectoryName(source)!,
                            "wxid_headless",
                            source,
                            DateTimeOffset.UtcNow,
                            1,
                            IsReparsePoint: false,
                            HasSnapshot: false)],
                        false,
                        3),
                },
                weixinProcessProbe: new FakeWeixinProcessProbe());
            services.Project.EnvironmentAssessment = new FakeEnvironmentWorkflow().Result;
            var page = new SourceSnapshotViewModel(services);
            await page.OnNavigatedToAsync();
            var view = new SourceSnapshotView { DataContext = page };
            view.Measure(new Size(1000, 700));
            view.Arrange(new Rect(0, 0, 1000, 700));

            Assert.Equal("wxid_headless", page.SelectedSourceCandidate?.AccountCandidate);
            Assert.False(page.IsAdvancedDetailsExpanded);
            Assert.NotNull(view.DataContext);
        });
    }

    [Fact]
    public async Task Fake_backend_runs_the_guided_flow_to_the_second_contact()
    {
        using var temporary = new TestTemporaryDirectory();
        var export = temporary.GetPath("export");
        var fakeSnapshot = new FakeSnapshotWorkflow();
        var fakeScan = new FakeScanWorkflow();
        var fakeExport = new FakeExportWorkflow();
        var setup = HeadlessTestHost.Dispatch(() =>
        {
            var root = new WorkflowCompositionRoot(
                new TestDoubles.SilentConfirmation(),
                environmentAssessment: new FakeEnvironmentWorkflow(),
                snapshot: fakeSnapshot,
                materialization: new FakeMaterializationWorkflow(),
                contactDiscovery: new FakeContactWorkflow(),
                voiceScan: fakeScan,
                voiceExport: fakeExport);
            var source = temporary.CreateDirectory(Path.Combine("wxid_owner_0000000000000000", "db_storage"));
            File.WriteAllBytes(Path.Combine(source, "messages.db"), [1, 2, 3]);
            var discovery = new FakeDataSourceDiscovery
            {
                Result = new WeixinDataSourceDiscoveryResult(
                    [new WeixinDataSourceCandidate(
                        Path.GetDirectoryName(source)!,
                        "wxid_owner",
                        source,
                        DateTimeOffset.UtcNow,
                        1,
                        IsReparsePoint: false,
                        HasSnapshot: false)],
                    WasTruncated: false,
                    VisitedDirectoryCount: 3),
            };
            var processProbe = new FakeWeixinProcessProbe();
            var services = new DesktopServices(
                root,
                new DesktopLog(temporary.Root),
                new RecentWorkspaceStore(temporary.Root),
                invokeOnUi: DirectInvokeAsync,
                dataSourceDiscovery: discovery,
                weixinProcessProbe: processProbe);
            return (Services: services, Main: new MainWindowViewModel(services));
        });

        try
        {
            var services = setup.Services;
            var main = setup.Main;
            var sourcePage = Assert.IsType<SourceSnapshotViewModel>(main.Pages[1]);
            var materialization = Assert.IsType<MaterializationViewModel>(main.Pages[2]);
            Assert.False(sourcePage.CanNavigate);
            Assert.False(materialization.CanNavigate);

            var environment = Assert.IsType<EnvironmentViewModel>(main.Pages[0]);
            await environment.AssessCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(WorkflowState.Completed, environment.RunHost.State);

            Assert.True(sourcePage.CanNavigate);
            main.SelectedPage = sourcePage;
            await main.NavigationTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("wxid_owner", sourcePage.SelectedSourceCandidate?.AccountCandidate);
            Assert.False(string.IsNullOrWhiteSpace(sourcePage.OutputDirectory));
            await sourcePage.CreateSnapshotCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(WorkflowState.Completed, sourcePage.RunHost.State);
            Assert.Equal(false, fakeSnapshot.LastRequest?.AllowLiveSource);

            Assert.True(materialization.CanNavigate);
            var processProbe = Assert.IsType<FakeWeixinProcessProbe>(services.WeixinProcessProbe);
            processProbe.Running = [new WeChatVoice.Windows.WeChatProcessInfo(1234, "WeChat")];
            materialization.RefreshWeixinStateCommand.Execute(null);
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
            scan.FromText = "2026-01-01T00:00:00Z";
            scan.ToText = "2026-01-31T23:59:59Z";
            scan.MaximumResultsText = "100";
            await scan.ScanCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(VoiceDirection.Incoming, fakeScan.LastRequest?.Direction);
            Assert.Equal(100, fakeScan.LastRequest?.MaximumResults);
            Assert.Equal("contact-b", services.Project.SelectionPlan?.ContactId);

            var exportPage = Assert.IsType<ExportViewModel>(main.Pages[5]);
            exportPage.OutputDirectory = export;
            await exportPage.ExportCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(WorkflowState.Completed, exportPage.RunHost.State);
            Assert.Equal("wxid_b", fakeExport.LastRequest?.ContactUsername);
            Assert.Equal(VoiceDirection.Incoming, fakeExport.LastRequest?.Direction);
            Assert.Equal(100, fakeExport.LastRequest?.MaximumResults);
            Assert.Equal(fakeScan.LastRequest?.From, fakeExport.LastRequest?.From);
            Assert.Equal(fakeScan.LastRequest?.To, fakeExport.LastRequest?.To);
            Assert.Equal(services.Project.SelectionPlan?.ResultSetFingerprint, fakeExport.LastRequest?.ExpectedResultSetFingerprint);
            main.SelectedPage = exportPage;
            Assert.Same(exportPage, main.SelectedPage);
        }
        finally
        {
            await setup.Services.DisposeAsync();
        }
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
