using WeChatVoice.Core.Errors;
using WeChatVoice.Desktop.Infrastructure;
using WeChatVoice.Desktop.ViewModels;
using WeChatVoice.Workflows.Composition;
using WeChatVoice.Workflows.Workflows;

namespace WeChatVoice.Desktop.Tests;

public sealed class SourceSnapshotViewModelTests
{
    [Fact]
    public async Task One_complete_selectable_source_is_selected_and_gets_private_default_output()
    {
        using var temporary = new TemporaryDirectory();
        var source = temporary.CreateSource("wxid_one_0000000000000001", "one.db");
        var discovery = new FakeDataSourceDiscovery
        {
            Result = Result(Candidate(source, "wxid_one", databaseCount: 1), visited: 4),
        };
        await using var services = CreateServices(temporary, discovery, new FakeWeixinProcessProbe());
        var page = CreatePage(services);

        await page.OnNavigatedToAsync();

        Assert.Equal(SourceSnapshotPageState.ReadyForSnapshot, page.State);
        Assert.Equal(source, page.SourceDirectory);
        Assert.Equal("wxid_one", page.SelectedSourceCandidate?.AccountCandidate);
        Assert.False(string.IsNullOrWhiteSpace(page.OutputDirectory));
        Assert.DoesNotContain("wxid_one", page.OutputDirectory!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(source, services.Project.SourceDirectory);
        Assert.False(string.IsNullOrWhiteSpace(services.Project.SourceAccountFingerprint));
        Assert.True(page.CanCreateSnapshot);
    }

    [Fact]
    public async Task Multiple_sources_remain_unselected_until_the_second_account_is_clicked()
    {
        using var temporary = new TemporaryDirectory();
        var first = temporary.CreateSource("wxid_first_0000000000000001", "first.db");
        var second = temporary.CreateSource("wxid_second_0000000000000002", "second.db");
        var discovery = new FakeDataSourceDiscovery
        {
            Result = new WeixinDataSourceDiscoveryResult(
                [Candidate(first, "wxid_first", 1), Candidate(second, "wxid_second", 1)],
                WasTruncated: false,
                VisitedDirectoryCount: 8),
        };
        await using var services = CreateServices(temporary, discovery, new FakeWeixinProcessProbe());
        var page = CreatePage(services);

        await page.OnNavigatedToAsync();

        Assert.Equal(SourceSnapshotPageState.MultipleSourcesRequireSelection, page.State);
        Assert.Null(page.SelectedSourceCandidate);
        Assert.Null(page.SourceDirectory);
        Assert.False(page.CanCreateSnapshot);

        page.SelectedSourceCandidate = page.SourceCandidates[1];

        Assert.Equal(second, page.SourceDirectory);
        Assert.Equal(second, services.Project.SourceDirectory);
        Assert.Equal("wxid_second", page.AccountCandidate);
        Assert.NotEqual(first, page.SourceDirectory);
        Assert.NotNull(page.OutputDirectory);
    }

    [Fact]
    public async Task No_source_does_not_create_a_fake_path()
    {
        using var temporary = new TemporaryDirectory();
        var discovery = new FakeDataSourceDiscovery
        {
            Result = new WeixinDataSourceDiscoveryResult([], false, 3),
        };
        await using var services = CreateServices(temporary, discovery, new FakeWeixinProcessProbe());
        var page = CreatePage(services);

        await page.OnNavigatedToAsync();

        Assert.Equal(SourceSnapshotPageState.NoSourceFound, page.State);
        Assert.Null(page.SourceDirectory);
        Assert.Null(page.OutputDirectory);
        Assert.True(page.IsNoSourceFound);
    }

    [Fact]
    public async Task Invalid_candidates_are_not_automatically_selected()
    {
        using var temporary = new TemporaryDirectory();
        var source = temporary.CreateSource("wxid_invalid_0000000000000003", "invalid.db");
        var discovery = new FakeDataSourceDiscovery
        {
            Result = new WeixinDataSourceDiscoveryResult(
                [
                    Candidate(source, "wxid_invalid", 0),
                    Candidate(source, "wxid_reparse", 1) with { IsReparsePoint = true },
                ],
                false,
                4),
        };
        await using var services = CreateServices(temporary, discovery, new FakeWeixinProcessProbe());
        var page = CreatePage(services);

        await page.OnNavigatedToAsync();

        Assert.Equal(SourceSnapshotPageState.SourceInvalid, page.State);
        Assert.Null(page.SelectedSourceCandidate);
        Assert.All(page.SourceCandidates, candidate => Assert.False(candidate.IsSelectable));
        Assert.Contains("数据库", page.SourceCandidates[0].UnavailableReason, StringComparison.Ordinal);
        Assert.Contains("Reparse", page.SourceCandidates[1].UnavailableReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Truncated_discovery_is_explicit_and_never_silently_picks_the_only_visible_account()
    {
        using var temporary = new TemporaryDirectory();
        var source = temporary.CreateSource("wxid_partial_0000000000000004", "partial.db");
        var discovery = new FakeDataSourceDiscovery
        {
            Result = Result(Candidate(source, "wxid_partial", 1), visited: 5, truncated: true),
        };
        await using var services = CreateServices(temporary, discovery, new FakeWeixinProcessProbe());
        var page = CreatePage(services);

        await page.OnNavigatedToAsync();

        Assert.Equal(SourceSnapshotPageState.DiscoveryIncomplete, page.State);
        Assert.Null(page.SelectedSourceCandidate);
        Assert.Contains("可能不完整", page.DiscoveryWarning, StringComparison.Ordinal);
        Assert.Equal(5, page.VisitedDirectoryCount);

        page.SelectedSourceCandidate = page.SourceCandidates[0];
        Assert.Equal(source, page.SourceDirectory);
    }

    [Fact]
    public async Task Running_weixin_blocks_creation_and_exit_recheck_enables_it_without_reopening_the_page()
    {
        using var temporary = new TemporaryDirectory();
        var source = temporary.CreateSource("wxid_running_0000000000000005", "running.db");
        var probe = new FakeWeixinProcessProbe
        {
            Running = [new WeChatVoice.Windows.WeChatProcessInfo(42, "WeChat")],
        };
        var discovery = new FakeDataSourceDiscovery
        {
            Result = Result(Candidate(source, "wxid_running", 1), visited: 3),
        };
        await using var services = CreateServices(temporary, discovery, probe);
        var page = CreatePage(services);

        await page.OnNavigatedToAsync();
        Assert.Equal(SourceSnapshotPageState.WaitingForWeixinExit, page.State);
        Assert.False(page.CanCreateSnapshot);

        probe.Running = [];
        await page.RecheckWeixinAfterExitCommand.ExecuteAsync(null);

        Assert.Equal(SourceSnapshotPageState.ReadyForSnapshot, page.State);
        Assert.True(page.CanCreateSnapshot);
    }

    [Fact]
    public async Task Returning_to_the_page_reuses_the_discovery_and_manual_refresh_forces_one_new_scan()
    {
        using var temporary = new TemporaryDirectory();
        var source = temporary.CreateSource("wxid_lifecycle_0000000000000006", "lifecycle.db");
        var discovery = new FakeDataSourceDiscovery
        {
            Result = Result(Candidate(source, "wxid_lifecycle", 1), visited: 2),
        };
        await using var services = CreateServices(temporary, discovery, new FakeWeixinProcessProbe());
        var main = new MainWindowViewModel(services);
        var sourcePage = Assert.IsType<SourceSnapshotViewModel>(main.Pages[2]);
        services.Project.EnvironmentAssessment = new FakeEnvironmentWorkflow().Result;

        main.SelectedPage = sourcePage;
        await main.NavigationTask;
        Assert.Equal(1, discovery.CallCount);

        main.SelectedPage = main.Pages[0];
        await main.NavigationTask;
        main.SelectedPage = sourcePage;
        await main.NavigationTask;
        Assert.Equal(1, discovery.CallCount);

        await sourcePage.DiscoverSourcesCommand.ExecuteAsync(null);
        Assert.Equal(2, discovery.CallCount);
    }

    [Fact]
    public async Task Leaving_the_page_cancels_an_inflight_discovery()
    {
        using var temporary = new TemporaryDirectory();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var discovery = new FakeDataSourceDiscovery
        {
            DiscoverOverride = async cancellationToken =>
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new WeixinDataSourceDiscoveryResult([], false, 1);
            },
        };
        await using var services = CreateServices(temporary, discovery, new FakeWeixinProcessProbe());
        var page = CreatePage(services);
        var activation = page.OnNavigatedToAsync();

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await page.OnNavigatedFromAsync();
        await activation.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, discovery.CallCount);
        Assert.Equal(SourceSnapshotPageState.Failed, page.State);
    }

    [Fact]
    public async Task Creation_checks_weixin_again_before_starting_the_snapshot_workflow()
    {
        using var temporary = new TemporaryDirectory();
        var source = temporary.CreateSource("wxid_owner_0000000000000007", "second-check.db");
        var probe = new FakeWeixinProcessProbe();
        var snapshot = new FakeSnapshotWorkflow();
        var discovery = new FakeDataSourceDiscovery
        {
            Result = Result(Candidate(source, "wxid_owner", 1), visited: 2),
        };
        await using var services = CreateServices(temporary, discovery, probe, snapshot);
        var page = CreatePage(services);
        await page.OnNavigatedToAsync();

        probe.Running = [new WeChatVoice.Windows.WeChatProcessInfo(43, "WeChat")];
        await page.CreateSnapshotCommand.ExecuteAsync(null);

        Assert.Equal(ErrorCode.WeixinStillRunning, page.RunHost.LastErrorCode);
        Assert.Equal(SourceSnapshotPageState.WaitingForWeixinExit, page.State);
        Assert.Null(snapshot.LastRequest);

        probe.Running = [];
        await page.RecheckWeixinAfterExitCommand.ExecuteAsync(null);
        await page.CreateSnapshotCommand.ExecuteAsync(null);
        Assert.Equal(WorkflowState.Completed, page.RunHost.State);
        Assert.NotNull(snapshot.LastRequest);
        Assert.False(snapshot.LastRequest!.AllowLiveSource);
    }

    [Fact]
    public async Task Manual_folder_picker_is_a_validated_fallback()
    {
        using var temporary = new TemporaryDirectory();
        var source = temporary.CreateSource("wxid_manual_0000000000000009", "manual.db");
        var picker = new FakeFolderPicker { NextPath = Path.GetDirectoryName(source) };
        var discovery = new FakeDataSourceDiscovery
        {
            Result = new WeixinDataSourceDiscoveryResult([], false, 0),
        };
        await using var services = CreateServices(
            temporary,
            discovery,
            new FakeWeixinProcessProbe(),
            folderPicker: picker);
        var page = CreatePage(services);
        await page.OnNavigatedToAsync();
        Assert.Equal(SourceSnapshotPageState.NoSourceFound, page.State);

        discovery.Result = Result(Candidate(source, "wxid_manual", 1), visited: 2);
        await page.BrowseSourceCommand.ExecuteAsync(null);

        Assert.Equal(source, page.SourceDirectory);
        Assert.Equal(SourceSnapshotPageState.ReadyForSnapshot, page.State);
        Assert.True(page.CanCreateSnapshot);
    }

    [Fact]
    public async Task Manual_folder_picker_rejects_anordinary_directory_without_inventing_a_source()
    {
        using var temporary = new TemporaryDirectory();
        var picker = new FakeFolderPicker { NextPath = temporary.Root };
        var discovery = new FakeDataSourceDiscovery
        {
            Result = new WeixinDataSourceDiscoveryResult([], false, 1),
        };
        await using var services = CreateServices(
            temporary,
            discovery,
            new FakeWeixinProcessProbe(),
            folderPicker: picker);
        var page = CreatePage(services);
        await page.OnNavigatedToAsync();

        await page.BrowseSourceCommand.ExecuteAsync(null);

        Assert.Equal(SourceSnapshotPageState.SourceInvalid, page.State);
        Assert.Equal(ErrorCode.SelectedDataSourceInvalid, page.DiscoveryErrorCode);
        Assert.Null(page.SourceDirectory);
        Assert.Null(page.OutputDirectory);
    }

    private static SourceSnapshotViewModel CreatePage(DesktopServices services)
    {
        services.Project.EnvironmentAssessment = new FakeEnvironmentWorkflow().Result;
        return new SourceSnapshotViewModel(services);
    }

    private static DesktopServices CreateServices(
        TemporaryDirectory temporary,
        FakeDataSourceDiscovery discovery,
        FakeWeixinProcessProbe probe,
        FakeSnapshotWorkflow? snapshot = null,
        FakeFolderPicker? folderPicker = null)
    {
        var root = new WorkflowCompositionRoot(
            new TestDoubles.SilentConfirmation(),
            environmentAssessment: new FakeEnvironmentWorkflow(),
            snapshot: snapshot ?? new FakeSnapshotWorkflow());
        return new DesktopServices(
            root,
            new DesktopLog(temporary.Root),
            new RecentWorkspaceStore(temporary.Root),
            invokeOnUi: DirectInvokeAsync,
            dataSourceDiscovery: discovery,
            weixinProcessProbe: probe,
            folderPicker: folderPicker);
    }

    private static WeixinDataSourceDiscoveryResult Result(
        WeixinDataSourceCandidate candidate,
        int visited,
        bool truncated = false)
        => new([candidate], truncated, visited);

    private static WeixinDataSourceCandidate Candidate(
        string source,
        string account,
        int databaseCount)
        => new(
            Path.GetDirectoryName(source)!,
            account,
            source,
            DateTimeOffset.UtcNow,
            databaseCount,
            IsReparsePoint: false,
            HasSnapshot: false);

    private static Task DirectInvokeAsync(Action action)
    {
        action();
        return Task.CompletedTask;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Root = Path.Combine(Path.GetTempPath(), "WeChatVoiceToolkit.SourceSnapshotTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string CreateSource(string accountDirectory, string databaseName)
        {
            var source = Directory.CreateDirectory(Path.Combine(Root, accountDirectory, "db_storage")).FullName;
            File.WriteAllBytes(Path.Combine(source, databaseName), [1, 2, 3]);
            return source;
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
