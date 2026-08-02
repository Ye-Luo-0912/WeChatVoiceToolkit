using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WeChatVoice.Core.Errors;
using WeChatVoice.Desktop.Infrastructure;
using WeChatVoice.Workflows.Workflows;

namespace WeChatVoice.Desktop.ViewModels;

/// <summary>
/// Source snapshot page: creates the stable file-level snapshot from the
/// Weixin <c>db_storage</c> directory and reports the derived account
/// candidate. The snapshot is the trusted input for every later step.
/// </summary>
public sealed partial class SourceSnapshotViewModel : PageViewModelBase
{
    public SourceSnapshotViewModel(DesktopServices services)
        : base(services)
    {
    }

    public override string Title => "源快照";

    [ObservableProperty]
    private string? _sourceDirectory;

    [ObservableProperty]
    private string? _outputDirectory;

    [ObservableProperty]
    private bool _allowLiveSource;

    [ObservableProperty]
    private string? _snapshotSummary;

    [ObservableProperty]
    private string? _accountCandidate;

    [ObservableProperty]
    private bool _isPotentiallyInconsistent;

    [ObservableProperty] private IReadOnlyList<WeixinDataSourceCandidate> _sourceCandidates = [];
    [ObservableProperty] private WeixinDataSourceCandidate? _selectedSourceCandidate;

    public bool IsLiveSourceAdvancedOptionVisible => false;

    [RelayCommand]
    private async Task DiscoverSourcesAsync()
    {
        var candidates = await Task.Run(() => Services.DataSourceDiscovery.Discover()).ConfigureAwait(true);
        SourceCandidates = candidates;
        SelectedSourceCandidate = null;
        SnapshotSummary = candidates.Count == 0 ? "未发现 Weixin db_storage。" : $"发现 {candidates.Count} 个数据源，请明确选择。";
    }

    partial void OnSelectedSourceCandidateChanged(WeixinDataSourceCandidate? value)
    {
        if (value is not null) SourceDirectory = value.DbStoragePath;
    }

    [RelayCommand]
    private Task CreateSnapshotAsync()
    {
        var sourceDirectory = SourceDirectory;
        var outputDirectory = OutputDirectory;
        var allowLiveSource = AllowLiveSource;
        if (!string.IsNullOrWhiteSpace(sourceDirectory)) Services.Project.ResetFromSource(sourceDirectory);
        return RunHost.RunAsync(
        async (context, cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(sourceDirectory) || string.IsNullOrWhiteSpace(outputDirectory))
            {
                throw new AppFailureException(ErrorCode.InvalidRequest, "Source and output directories are required.");
            }

            return await Workflows.Snapshot.RunAsync(
                new SnapshotWorkflowRequest(sourceDirectory, outputDirectory, AllowLiveSource: allowLiveSource, MaxAttempts: 3),
                context,
                cancellationToken).ConfigureAwait(false);
        },
        result =>
        {
            Services.Project.Snapshot = result;
            Services.Project.SnapshotDirectory = outputDirectory;
            AccountCandidate = result.SourceIdentity?.AccountCandidate;
            IsPotentiallyInconsistent = result.Manifest.PotentiallyInconsistent;
            SnapshotSummary = $"快照 {result.Manifest.SnapshotId[..16]}… 已创建：{result.Manifest.Files.Count} 个文件"
                + (result.SourceIdentity?.AccountCandidate is { } candidate ? $"；检测到账号：{candidate}" : string.Empty)
                + (result.Manifest.PotentiallyInconsistent ? "；⚠ 源为活动状态（potentiallyInconsistent）" : string.Empty);
        });
    }

    [RelayCommand]
    private async Task BrowseSourceAsync()
    {
        var path = await Services.FolderPicker.PickFolderAsync("选择 Weixin db_storage 源目录").ConfigureAwait(true);
        if (path is not null)
        {
            SourceDirectory = path;
        }
    }

    [RelayCommand]
    private async Task BrowseOutputAsync()
    {
        var path = await Services.FolderPicker.PickFolderAsync("选择快照输出目录").ConfigureAwait(true);
        if (path is not null)
        {
            OutputDirectory = path;
        }
    }
}
