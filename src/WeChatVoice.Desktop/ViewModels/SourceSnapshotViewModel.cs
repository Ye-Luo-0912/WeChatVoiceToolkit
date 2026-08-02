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

    public override bool CanNavigate => Services.Project.EnvironmentAssessment is not null;

    public override string? NavigationHint => CanNavigate ? null : "请先完成环境检测";

    [ObservableProperty]
    private string? _sourceDirectory;

    [ObservableProperty]
    private string? _outputDirectory;

    [ObservableProperty]
    private string? _snapshotSummary;

    [ObservableProperty]
    private string _pathValidationSummary = "请选择源目录和快照输出目录。";

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
        var candidates = await Services.DataSourceDiscovery.DiscoverAsync().ConfigureAwait(true);
        SourceCandidates = candidates;
        SelectedSourceCandidate = null;
        SnapshotSummary = candidates.Count == 0 ? "未发现 Weixin db_storage。" : $"发现 {candidates.Count} 个数据源，请明确选择；不会自动使用最近修改的账号。";
    }

    partial void OnSelectedSourceCandidateChanged(WeixinDataSourceCandidate? value)
    {
        if (value is not null) SourceDirectory = value.DbStoragePath;
    }

    partial void OnSourceDirectoryChanged(string? value) => RefreshPathValidation();
    partial void OnOutputDirectoryChanged(string? value) => RefreshPathValidation();

    [RelayCommand]
    private Task CreateSnapshotAsync()
    {
        var sourceDirectory = SourceDirectory;
        var outputDirectory = OutputDirectory;
        var discoveredSourceCount = SourceCandidates.Count;
        var selectedSourcePath = SelectedSourceCandidate?.DbStoragePath;
        if (!string.IsNullOrWhiteSpace(sourceDirectory)) Services.Project.ResetFromSource(sourceDirectory);
        return RunHost.RunAsync(
        async (context, cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(sourceDirectory) || string.IsNullOrWhiteSpace(outputDirectory))
            {
                throw new AppFailureException(ErrorCode.InvalidRequest, "Source and output directories are required.");
            }

            if (discoveredSourceCount > 1 && selectedSourcePath is null)
            {
                throw new AppFailureException(ErrorCode.InvalidRequest, "发现多个账号数据源，请先显式选择一个账号。");
            }

            if (selectedSourcePath is not null
                && !string.Equals(Path.GetFullPath(selectedSourcePath), Path.GetFullPath(sourceDirectory), StringComparison.OrdinalIgnoreCase))
            {
                throw new AppFailureException(ErrorCode.InvalidRequest, "源目录已改变，请重新选择发现的账号目录。");
            }

            var validation = DesktopPathValidator.ValidateSnapshotPaths(sourceDirectory, outputDirectory);
            if (!validation.IsValid)
            {
                throw new AppFailureException(ErrorCode.InvalidRequest, validation.Error ?? "源路径校验失败。");
            }

            return await Workflows.Snapshot.RunAsync(
                new SnapshotWorkflowRequest(sourceDirectory, outputDirectory, AllowLiveSource: false, MaxAttempts: 3),
                context,
                cancellationToken).ConfigureAwait(false);
        },
        result =>
        {
            Services.Project.Snapshot = result;
            Services.Project.SnapshotDirectory = outputDirectory;
            Services.RecentWorkspaces.AddSnapshot(
                result.Manifest.SourceDirectory,
                outputDirectory!,
                result.Manifest.SnapshotId);
            AccountCandidate = result.SourceIdentity?.AccountCandidate;
            IsPotentiallyInconsistent = result.Manifest.PotentiallyInconsistent;
            SnapshotSummary = $"快照 {result.Manifest.SnapshotId[..16]}… 已创建：{result.Manifest.Files.Count} 个文件"
                + (result.SourceIdentity?.AccountCandidate is { } candidate ? $"；检测到账号：{candidate}" : string.Empty)
                + (result.Manifest.PotentiallyInconsistent ? "；⚠ 源为活动状态（potentiallyInconsistent）" : string.Empty);
            RefreshPathValidation();
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

    private void RefreshPathValidation()
    {
        var result = DesktopPathValidator.ValidateSnapshotPaths(SourceDirectory, OutputDirectory);
        PathValidationSummary = result.IsValid
            ? result.AvailableFreeBytes is { } bytes
                ? $"路径可用；目标卷剩余空间约 {bytes / (1024d * 1024 * 1024):F1} GiB。"
                : "路径可用；空间信息暂不可用。"
            : result.Error ?? "路径校验失败。";
    }
}
