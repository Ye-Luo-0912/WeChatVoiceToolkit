using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    [RelayCommand]
    private Task CreateSnapshotAsync() => RunHost.RunAsync(async (context, cancellationToken) =>
    {
        if (string.IsNullOrWhiteSpace(SourceDirectory) || string.IsNullOrWhiteSpace(OutputDirectory))
        {
            throw new ArgumentException("请填写源目录与输出目录。");
        }

        var result = await Workflows.Snapshot.RunAsync(
            new SnapshotWorkflowRequest(SourceDirectory, OutputDirectory, AllowLiveSource: AllowLiveSource, MaxAttempts: 3),
            context,
            cancellationToken).ConfigureAwait(false);
        AccountCandidate = result.SourceIdentity?.AccountCandidate;
        IsPotentiallyInconsistent = result.Manifest.PotentiallyInconsistent;
        SnapshotSummary = $"快照 {result.Manifest.SnapshotId[..16]}… 已创建：{result.Manifest.Files.Count} 个文件"
            + (result.SourceIdentity?.AccountCandidate is { } candidate ? $"；检测到账号：{candidate}" : string.Empty)
            + (result.Manifest.PotentiallyInconsistent ? "；⚠ 源为活动状态（potentiallyInconsistent）" : string.Empty);
    });
}
