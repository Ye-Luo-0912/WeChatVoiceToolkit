using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WeChatVoice.Core.Errors;
using WeChatVoice.Core.Models;
using WeChatVoice.Desktop.Infrastructure;
using WeChatVoice.Workflows.Workflows;

namespace WeChatVoice.Desktop.ViewModels;

public enum SourceSnapshotPageState
{
    Discovering,
    NoSourceFound,
    SingleSourceSelected,
    MultipleSourcesRequireSelection,
    SourceInvalid,
    DiscoveryIncomplete,
    WaitingForWeixinExit,
    ReadyForSnapshot,
    CreatingSnapshot,
    SnapshotCompleted,
    Failed,
}

/// <summary>
/// Guided source snapshot page. Automatic discovery is started by the
/// navigation host, not by this view-model constructor. The ordinary page
/// deals in account summaries; raw paths remain in the advanced section.
/// </summary>
public sealed partial class SourceSnapshotViewModel : PageViewModelBase
{
    private bool _applyingDiscovery;
    private bool _applyingSourceSelection;
    private bool _applyingOutputSelection;
    private CancellationTokenSource? _discoveryCancellation;
    private bool _hasCompletedDiscovery;

    public SourceSnapshotViewModel(DesktopServices services)
        : base(services)
    {
        RunHost.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName is nameof(WorkflowRunHost.IsRunning)
                or nameof(WorkflowRunHost.State))
            {
                NotifyDerivedProperties();
            }
        };
    }

    public override string Title => "源快照";

    public override bool CanNavigate => Services.Project.EnvironmentAssessment is not null;

    public override string? NavigationHint => CanNavigate ? null : "请先完成环境检测";

    [ObservableProperty]
    private SourceSnapshotPageState _state = SourceSnapshotPageState.Discovering;

    [ObservableProperty]
    private string? _sourceDirectory;

    [ObservableProperty]
    private string? _outputDirectory;

    [ObservableProperty]
    private string? _snapshotSummary;

    [ObservableProperty]
    private string _pathValidationSummary = "选择微信账号后将自动校验快照保存位置。";

    [ObservableProperty]
    private string? _accountCandidate;

    [ObservableProperty]
    private bool _isPotentiallyInconsistent;

    [ObservableProperty]
    private IReadOnlyList<WeixinDataSourceCandidate> _sourceCandidates = [];

    [ObservableProperty]
    private WeixinDataSourceCandidate? _selectedSourceCandidate;

    [ObservableProperty]
    private bool _isAdvancedDetailsExpanded;

    [ObservableProperty]
    private bool _isWeixinRunning;

    [ObservableProperty]
    private string _weixinStatusSummary = "尚未检测微信运行状态。";

    [ObservableProperty]
    private string _discoverySummary = "进入页面后自动查找微信数据。";

    [ObservableProperty]
    private string? _discoveryWarning;

    [ObservableProperty]
    private int _visitedDirectoryCount;

    [ObservableProperty]
    private bool _wasDiscoveryTruncated;

    [ObservableProperty]
    private ErrorCode? _discoveryErrorCode;

    [ObservableProperty]
    private bool _isCustomOutputDirectory;

    public bool IsLiveSourceAdvancedOptionVisible => false;

    public bool IsDiscoveryInProgress => State == SourceSnapshotPageState.Discovering;

    public bool IsBusy => IsDiscoveryInProgress || RunHost.IsRunning;

    public bool CanStartDiscovery => !IsDiscoveryInProgress && !RunHost.IsRunning;

    public bool IsDiscoveryIncomplete => WasDiscoveryTruncated || State == SourceSnapshotPageState.DiscoveryIncomplete;

    public bool IsNoSourceFound => State == SourceSnapshotPageState.NoSourceFound;

    public bool IsSourceInvalid => State == SourceSnapshotPageState.SourceInvalid;

    public bool IsMultipleSourcesRequireSelection
        => State == SourceSnapshotPageState.MultipleSourcesRequireSelection;

    public bool HasSelectedSource => SelectedSourceCandidate is not null;

    public bool IsAccountListVisible
        => SourceCandidates.Count > 0
            && (SelectedSourceCandidate is null || SourceCandidates.Count > 1 || IsDiscoveryIncomplete);

    public bool CanCreateSnapshot
        => State == SourceSnapshotPageState.ReadyForSnapshot
            && SelectedSourceCandidate is { IsSelectable: true }
            && !IsWeixinRunning
            && !RunHost.IsRunning
            && !string.IsNullOrWhiteSpace(SourceDirectory)
            && !string.IsNullOrWhiteSpace(OutputDirectory)
            && DesktopPathValidator.ValidateSnapshotPaths(SourceDirectory, OutputDirectory).IsValid;

    public string StateSummary => State switch
    {
        SourceSnapshotPageState.Discovering => "正在查找 Weixin 数据……",
        SourceSnapshotPageState.NoSourceFound => "未自动找到微信数据，请选择数据目录继续。",
        SourceSnapshotPageState.SingleSourceSelected => "已自动找到一个微信账号。",
        SourceSnapshotPageState.MultipleSourcesRequireSelection => "发现多个微信账号，请明确选择一个。",
        SourceSnapshotPageState.SourceInvalid => "发现的数据目录不可用于创建稳定快照。",
        SourceSnapshotPageState.DiscoveryIncomplete => "自动发现未完成，结果可能不完整。",
        SourceSnapshotPageState.WaitingForWeixinExit => "请完全退出微信，以创建一致快照。",
        SourceSnapshotPageState.ReadyForSnapshot => "微信已退出，可以创建稳定快照。",
        SourceSnapshotPageState.CreatingSnapshot => "正在创建稳定快照……",
        SourceSnapshotPageState.SnapshotCompleted => "快照已完成，现在可以重新打开微信。",
        SourceSnapshotPageState.Failed => "快照操作失败，请检查状态后重试。",
        _ => "",
    };

    public string SnapshotLocationSummary
        => string.IsNullOrWhiteSpace(OutputDirectory)
            ? "选择微信账号后将保存到应用数据目录。"
            : IsCustomOutputDirectory
                ? "快照将保存到自定义位置。"
                : "快照将保存到应用数据目录。";

    public string DetailedDiscoverySummary
        => WasDiscoveryTruncated
            ? $"本次已检查 {VisitedDirectoryCount} 个目录，但达到搜索预算；结果可能不完整。"
            : $"本次已检查 {VisitedDirectoryCount} 个目录。";

    partial void OnStateChanged(SourceSnapshotPageState value)
        => NotifyDerivedProperties();

    partial void OnWasDiscoveryTruncatedChanged(bool value)
        => NotifyDerivedProperties();

    partial void OnSelectedSourceCandidateChanged(WeixinDataSourceCandidate? value)
    {
        NotifyDerivedProperties();
        if (_applyingDiscovery || _applyingSourceSelection)
        {
            return;
        }

        if (value is null)
        {
            ClearSourceSelection();
            State = SourceCandidates.Count > 1
                ? SourceSnapshotPageState.MultipleSourcesRequireSelection
                : SourceSnapshotPageState.NoSourceFound;
            return;
        }

        if (!value.IsSelectable)
        {
            _applyingSourceSelection = true;
            try
            {
                SelectedSourceCandidate = null;
            }
            finally
            {
                _applyingSourceSelection = false;
            }

            State = SourceSnapshotPageState.SourceInvalid;
            return;
        }

        ApplySelectedCandidate(value);
    }

    partial void OnSourceDirectoryChanged(string? value)
    {
        RefreshPathValidation();
        if (_applyingSourceSelection || _applyingDiscovery || value is null)
        {
            return;
        }

        if (SelectedSourceCandidate is not null
            && !PathsEqual(value, SelectedSourceCandidate.DbStoragePath))
        {
            ClearSourceSelection();
            State = SourceSnapshotPageState.SourceInvalid;
            SnapshotSummary = "源目录已改变，请使用备用选择入口重新验证微信数据目录。";
        }
    }

    partial void OnOutputDirectoryChanged(string? value)
    {
        RefreshPathValidation();
        if (!_applyingOutputSelection && value is not null)
        {
            IsCustomOutputDirectory = true;
        }

        if (value is not null
            && SelectedSourceCandidate is not null
            && State == SourceSnapshotPageState.Failed
            && DiscoveryErrorCode == ErrorCode.SnapshotOutputInvalid)
        {
            RefreshWeixinStatus();
        }

        NotifyDerivedProperties();
    }

    partial void OnSourceCandidatesChanged(IReadOnlyList<WeixinDataSourceCandidate> value)
        => NotifyDerivedProperties();

    partial void OnIsWeixinRunningChanged(bool value)
        => NotifyDerivedProperties();

    partial void OnIsCustomOutputDirectoryChanged(bool value)
        => NotifyDerivedProperties();

    public override async Task OnNavigatedToAsync(CancellationToken cancellationToken = default)
    {
        if (HasValidSelectedCandidate())
        {
            RefreshWeixinStatus();
            return;
        }

        if (_hasCompletedDiscovery)
        {
            if (SelectedSourceCandidate is not null)
            {
                await DiscoverSourcesCoreAsync(force: true, cancellationToken).ConfigureAwait(true);
                return;
            }

            RefreshWeixinStatus();
            return;
        }

        await DiscoverSourcesCoreAsync(force: false, cancellationToken).ConfigureAwait(true);
    }

    public override Task OnNavigatedFromAsync(CancellationToken cancellationToken = default)
    {
        _discoveryCancellation?.Cancel();
        if (RunHost.CanCancel)
        {
            RunHost.CancelCommand.Execute(null);
        }

        return Task.CompletedTask;
    }

    [RelayCommand]
    private Task DiscoverSourcesAsync()
    {
        // This command is the explicit refresh boundary. Clear only the
        // snapshot and downstream state; a normal page revisit keeps the
        // verified local snapshot and avoids another multi-gigabyte copy.
        Services.Project.Snapshot = null;
        Services.Project.SnapshotDirectory = null;
        OutputDirectory = null;
        SelectedSourceCandidate = null;
        _hasCompletedDiscovery = false;
        return DiscoverSourcesCoreAsync(force: true, CancellationToken.None);
    }

    [RelayCommand]
    private void CancelDiscovery()
        => _discoveryCancellation?.Cancel();

    [RelayCommand]
    private async Task RecheckWeixinAfterExitAsync()
    {
        RefreshWeixinStatus();
        await Task.CompletedTask.ConfigureAwait(true);
    }

    private async Task DiscoverSourcesCoreAsync(bool force, CancellationToken cancellationToken)
    {
        if (!force && _hasCompletedDiscovery)
        {
            RefreshWeixinStatus();
            return;
        }

        State = SourceSnapshotPageState.Discovering;
        DiscoveryErrorCode = null;
        DiscoveryWarning = null;
        DiscoverySummary = "正在查找 Weixin 数据……";
        NotifyDerivedProperties();

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _discoveryCancellation = linkedCancellation;
        try
        {
            var result = await Services.DataSourceDiscovery.DiscoverDetailedAsync(
                cancellationToken: linkedCancellation.Token).ConfigureAwait(true);
            ApplyDiscoveryResult(result);
            _hasCompletedDiscovery = true;
        }
        catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            State = SourceSnapshotPageState.Failed;
            DiscoverySummary = "数据发现已取消。";
        }
        catch (Exception)
        {
            _hasCompletedDiscovery = false;
            DiscoveryErrorCode = ErrorCode.DataSourceDiscoveryFailed;
            State = SourceSnapshotPageState.Failed;
            DiscoverySummary = "自动发现微信数据失败，请重新检测或使用备用选择入口。";
            Services.Log.ErrorCode(ErrorCode.DataSourceDiscoveryFailed);
        }
        finally
        {
            if (ReferenceEquals(_discoveryCancellation, linkedCancellation))
            {
                _discoveryCancellation = null;
            }
        }
    }

    private void ApplyDiscoveryResult(
        WeixinDataSourceDiscoveryResult result,
        bool manualSelection = false)
    {
        ArgumentNullException.ThrowIfNull(result);
        var previousPath = SelectedSourceCandidate?.DbStoragePath;
        WasDiscoveryTruncated = result.WasTruncated;
        VisitedDirectoryCount = result.VisitedDirectoryCount;
        DiscoverySummary = result.Candidates.Count == 0
            ? "未自动找到微信数据。"
            : $"已发现 {result.Candidates.Count} 个数据目录。";
        DiscoveryWarning = result.WasTruncated
            ? $"搜索达到时间或目录预算，以下结果可能不完整（已检查 {result.VisitedDirectoryCount} 个目录）。"
            : null;
        DiscoveryErrorCode = result.WasTruncated ? ErrorCode.DataSourceDiscoveryTruncated : null;

        _applyingDiscovery = true;
        try
        {
            SourceCandidates = result.Candidates;
            SelectedSourceCandidate = null;
        }
        finally
        {
            _applyingDiscovery = false;
        }

        var selectable = result.Candidates.Where(static item => item.IsSelectable).ToArray();
        var preserved = !string.IsNullOrWhiteSpace(previousPath)
            ? selectable.FirstOrDefault(item => PathsEqual(item.DbStoragePath, previousPath))
            : null;
        var candidateToSelect = preserved
            ?? (!result.WasTruncated && selectable.Length == 1 ? selectable[0] : null);

        if (candidateToSelect is not null)
        {
            SelectedSourceCandidate = candidateToSelect;
            return;
        }

        ClearSourceSelection();
        State = result.WasTruncated
            ? SourceSnapshotPageState.DiscoveryIncomplete
            : selectable.Length switch
            {
                0 when result.Candidates.Count == 0 => manualSelection
                    ? SourceSnapshotPageState.SourceInvalid
                    : SourceSnapshotPageState.NoSourceFound,
                0 => SourceSnapshotPageState.SourceInvalid,
                1 => SourceSnapshotPageState.DiscoveryIncomplete,
                _ => SourceSnapshotPageState.MultipleSourcesRequireSelection,
            };
        if (manualSelection && selectable.Length == 0)
        {
            DiscoveryErrorCode = ErrorCode.SelectedDataSourceInvalid;
            DiscoverySummary = "所选目录不符合微信数据布局、没有预期数据库，或无法安全读取。";
        }
        RefreshWeixinStatus();
        NotifyDerivedProperties();
    }

    private void ApplySelectedCandidate(WeixinDataSourceCandidate candidate)
    {
        var reusedSnapshot = false;
        var sourceChanged = !PathsEqual(SourceDirectory, candidate.DbStoragePath)
            || !string.Equals(AccountCandidate, candidate.AccountCandidate, StringComparison.Ordinal);
        if (sourceChanged)
        {
            _applyingSourceSelection = true;
            try
            {
                SourceDirectory = candidate.DbStoragePath;
                AccountCandidate = candidate.AccountCandidate;
                Services.Project.SetSourceSelection(
                    candidate.DbStoragePath,
                    candidate.AccountCandidate!,
                    SnapshotOutputDirectoryFactory.CreateAccountFingerprint(
                        candidate.AccountCandidate,
                        candidate.AccountDirectory));
            }
            finally
            {
                _applyingSourceSelection = false;
            }

            var outputAllocated = true;
            try
            {
                _applyingOutputSelection = true;
                OutputDirectory = Services.SnapshotOutputDirectories.CreateDefault(
                    candidate.DbStoragePath,
                    candidate.AccountCandidate,
                    candidate.AccountDirectory);
                IsCustomOutputDirectory = false;

                // A verified local snapshot is the default continuation point.
                // Creating a new snapshot is an explicit refresh action; merely
                // revisiting this page must not copy gigabytes again.
                if (TryLoadReusableSnapshot(candidate.DbStoragePath, out var reusable))
                {
                    reusedSnapshot = true;
                    OutputDirectory = reusable.Manifest.SnapshotDirectory;
                    Services.Project.Snapshot = reusable;
                    Services.Project.SnapshotDirectory = reusable.Manifest.SnapshotDirectory;
                    IsPotentiallyInconsistent = reusable.Manifest.PotentiallyInconsistent;
                    SnapshotSummary = "已复用本地快照；如需重新复制，请点击“自动查找”后重新创建。";
                    State = SourceSnapshotPageState.SnapshotCompleted;
                }
            }
            catch
            {
                outputAllocated = false;
                OutputDirectory = null;
                State = SourceSnapshotPageState.Failed;
                DiscoveryErrorCode = ErrorCode.SnapshotOutputInvalid;
                SnapshotSummary = "无法分配安全的默认快照保存位置，请在高级设置中更改保存位置。";
            }
            finally
            {
                _applyingOutputSelection = false;
            }

            if (!outputAllocated)
            {
                NotifyDerivedProperties();
                return;
            }
        }

        if (!reusedSnapshot)
        {
            State = SourceSnapshotPageState.SingleSourceSelected;
            SnapshotSummary = "已自动找到一个微信账号。";
        }
        RefreshWeixinStatus();
        RefreshPathValidation();
        NotifyDerivedProperties();
    }

    private void ClearSourceSelection()
    {
        _applyingSourceSelection = true;
        _applyingOutputSelection = true;
        try
        {
            SourceDirectory = null;
            OutputDirectory = null;
            AccountCandidate = null;
            IsCustomOutputDirectory = false;
            Services.Project.ClearSourceSelection();
        }
        finally
        {
            _applyingOutputSelection = false;
            _applyingSourceSelection = false;
        }

        RefreshPathValidation();
        NotifyDerivedProperties();
    }

    private bool TryLoadReusableSnapshot(string sourceDirectory, out SnapshotWorkflowResult result)
    {
        result = null!;
        var recent = Services.RecentWorkspaces.FindSnapshotForSource(sourceDirectory);
        if (recent is null)
        {
            return false;
        }

        var manifestPath = Path.Combine(recent.SnapshotDirectory, ".wechatvoice", "snapshot-manifest.json");
        try
        {
            if (!File.Exists(manifestPath))
            {
                return false;
            }

            var manifest = JsonSerializer.Deserialize<SnapshotManifest>(File.ReadAllText(manifestPath));
            if (manifest is null
                || !string.Equals(Path.GetFullPath(manifest.SnapshotDirectory), Path.GetFullPath(recent.SnapshotDirectory), StringComparison.OrdinalIgnoreCase)
                || !string.Equals(manifest.SnapshotId, recent.SnapshotId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            result = new SnapshotWorkflowResult(
                manifest,
                SnapshotSourceIdentity.TryDerive(manifest.SourceDirectory, manifest.Files),
                manifestPath);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return false;
        }
    }

    [RelayCommand]
    private async Task BrowseSourceAsync()
    {
        var path = await Services.FolderPicker.PickFolderAsync("选择微信数据目录").ConfigureAwait(true);
        if (path is null)
        {
            return;
        }

        State = SourceSnapshotPageState.Discovering;
        DiscoverySummary = "正在验证所选微信数据目录……";
        DiscoveryWarning = null;
        DiscoveryErrorCode = null;
        using var manualCancellation = new CancellationTokenSource();
        _discoveryCancellation = manualCancellation;
        try
        {
            var result = await Services.DataSourceDiscovery.DiscoverDetailedAsync(
                [path],
                new WeixinDataSourceDiscoveryOptions(MaxDepth: 8, MaxDirectories: 5_000, Timeout: TimeSpan.FromSeconds(5)),
                manualCancellation.Token)
                .ConfigureAwait(true);
            ApplyDiscoveryResult(result, manualSelection: true);
            _hasCompletedDiscovery = true;
        }
        catch (OperationCanceledException)
        {
            State = SourceSnapshotPageState.Failed;
            DiscoverySummary = "所选数据目录验证已取消。";
        }
        catch (Exception)
        {
            DiscoveryErrorCode = ErrorCode.SelectedDataSourceInvalid;
            State = SourceSnapshotPageState.SourceInvalid;
            DiscoverySummary = "所选目录不符合微信数据布局或无法安全读取。";
            Services.Log.ErrorCode(ErrorCode.SelectedDataSourceInvalid);
        }
        finally
        {
            if (ReferenceEquals(_discoveryCancellation, manualCancellation))
            {
                _discoveryCancellation = null;
            }
        }
    }

    [RelayCommand]
    private async Task BrowseOutputAsync()
    {
        var path = await Services.FolderPicker.PickFolderAsync("选择快照保存位置").ConfigureAwait(true);
        if (path is not null)
        {
            _applyingOutputSelection = true;
            try
            {
                OutputDirectory = path;
                IsCustomOutputDirectory = true;
            }
            finally
            {
                _applyingOutputSelection = false;
            }
        }
    }

    [RelayCommand]
    private async Task CreateSnapshotAsync()
    {
        var sourceDirectory = SourceDirectory;
        var outputDirectory = OutputDirectory;
        var selectedCandidate = SelectedSourceCandidate;
        var expectedAccount = selectedCandidate?.AccountCandidate;
        State = SourceSnapshotPageState.CreatingSnapshot;
        NotifyDerivedProperties();

        await RunHost.RunAsync(
            async (context, cancellationToken) =>
            {
                if (selectedCandidate is not { IsSelectable: true }
                    || string.IsNullOrWhiteSpace(sourceDirectory)
                    || string.IsNullOrWhiteSpace(outputDirectory))
                {
                    throw new AppFailureException(
                        ErrorCode.SelectedDataSourceInvalid,
                        "A verified Weixin account and snapshot destination are required.");
                }

                if (Services.WeixinProcessProbe.ListRunning().Count > 0)
                {
                    throw new AppFailureException(
                        ErrorCode.WeixinStillRunning,
                        "Weixin must be fully closed before a stable snapshot can be created.");
                }

                if (!PathsEqual(sourceDirectory, selectedCandidate.DbStoragePath)
                    || !IsCurrentCandidateLayoutValid(selectedCandidate))
                {
                    throw new AppFailureException(
                        ErrorCode.SelectedDataSourceInvalid,
                        "The selected Weixin data source is no longer valid.");
                }

                var validation = DesktopPathValidator.ValidateSnapshotPaths(
                    sourceDirectory,
                    outputDirectory,
                    verifyCapacity: true);
                if (!validation.IsValid)
                {
                    throw new AppFailureException(
                        validation.AvailableFreeBytes is not null
                            ? ErrorCode.InsufficientDiskSpace
                            : ErrorCode.SnapshotOutputInvalid,
                        "The snapshot destination failed safety validation.");
                }

                var result = await Workflows.Snapshot.RunAsync(
                    new SnapshotWorkflowRequest(
                        sourceDirectory,
                        outputDirectory,
                        AllowLiveSource: false,
                        MaxAttempts: 3),
                    context,
                    cancellationToken).ConfigureAwait(false);
                if (result.SourceIdentity?.AccountCandidate is { } actualAccount
                    && !string.Equals(actualAccount, expectedAccount, StringComparison.Ordinal))
                {
                    throw new AppFailureException(
                        ErrorCode.SelectedDataSourceInvalid,
                        "The snapshot account identity did not match the selected account.");
                }

                return result;
            },
            result => ApplySnapshotResult(result, outputDirectory!));

        if (RunHost.LastErrorCode == ErrorCode.WeixinStillRunning)
        {
            RefreshWeixinStatus();
            State = SourceSnapshotPageState.WaitingForWeixinExit;
        }
        else switch (RunHost.State)
            {
                case WorkflowState.Cancelled:
                    RefreshWeixinStatus();
                    State = SelectedSourceCandidate is null
                        ? SourceSnapshotPageState.SourceInvalid
                        : IsWeixinRunning
                            ? SourceSnapshotPageState.WaitingForWeixinExit
                            : SourceSnapshotPageState.ReadyForSnapshot;
                    break;
                case WorkflowState.Failed:
                    State = RunHost.LastErrorCode switch
                    {
                        ErrorCode.SelectedDataSourceInvalid => SourceSnapshotPageState.SourceInvalid,
                        ErrorCode.SnapshotOutputInvalid or ErrorCode.InsufficientDiskSpace => SourceSnapshotPageState.Failed,
                        _ => SourceSnapshotPageState.Failed,
                    };
                    break;
                case WorkflowState.Idle:
                case WorkflowState.Running:
                case WorkflowState.AwaitingUser:
                case WorkflowState.Cancelling:
                case WorkflowState.Completed:
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

        NotifyDerivedProperties();
    }

    private void ApplySnapshotResult(SnapshotWorkflowResult result, string outputDirectory)
    {
        Services.Project.Snapshot = result;
        Services.Project.SnapshotDirectory = outputDirectory;
        Services.RecentWorkspaces.AddSnapshot(
            result.Manifest.SourceDirectory,
            outputDirectory,
            result.Manifest.SnapshotId);
        AccountCandidate = result.SourceIdentity?.AccountCandidate ?? AccountCandidate;
        IsPotentiallyInconsistent = result.Manifest.PotentiallyInconsistent;
        SnapshotSummary = $"快照已完成，共 {result.Manifest.Files.Count} 个文件。现在可以重新打开微信。";
        State = SourceSnapshotPageState.SnapshotCompleted;
        RefreshPathValidation();
    }

    private void RefreshWeixinStatus()
    {
        var running = Services.WeixinProcessProbe.ListRunning();
        IsWeixinRunning = running.Count > 0;
        WeixinStatusSummary = IsWeixinRunning
            ? "微信正在运行：请完全退出微信，以创建一致快照。"
            : "微信已退出，可以创建稳定快照。";

        if (SelectedSourceCandidate is not null
            && State is not (SourceSnapshotPageState.CreatingSnapshot or SourceSnapshotPageState.SnapshotCompleted))
        {
            State = IsWeixinRunning
                ? SourceSnapshotPageState.WaitingForWeixinExit
                : SourceSnapshotPageState.ReadyForSnapshot;
        }

        NotifyDerivedProperties();
    }

    private bool HasValidSelectedCandidate()
        => SelectedSourceCandidate is { IsSelectable: true } candidate
            && Directory.Exists(candidate.DbStoragePath)
            && IsCurrentCandidateLayoutValid(candidate);

    private static bool IsCurrentCandidateLayoutValid(WeixinDataSourceCandidate candidate)
    {
        try
        {
            var directory = new DirectoryInfo(candidate.DbStoragePath);
            if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }

            var identity = SnapshotSourceIdentity.TryDerive(candidate.DbStoragePath, []);
            if (identity?.AccountCandidate is null
                || !string.Equals(identity.AccountCandidate, candidate.AccountCandidate, StringComparison.Ordinal))
            {
                return false;
            }

            var pending = new Stack<DirectoryInfo>([directory]);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                if (current.EnumerateFiles("*.db", SearchOption.TopDirectoryOnly).Any(file => (file.Attributes & FileAttributes.ReparsePoint) == 0))
                {
                    return true;
                }

                foreach (var child in current.EnumerateDirectories())
                {
                    if ((child.Attributes & FileAttributes.ReparsePoint) == 0)
                    {
                        pending.Push(child);
                    }
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return false;
    }

    private void RefreshPathValidation()
    {
        var result = DesktopPathValidator.ValidateSnapshotPaths(SourceDirectory, OutputDirectory);
        PathValidationSummary = result.IsValid
            ? result.AvailableFreeBytes is { } bytes
                ? $"保存位置可用；目标卷剩余空间约 {bytes / (1024d * 1024 * 1024):F1} GiB。"
                : "保存位置可用；空间信息暂不可用。"
            : result.Error ?? "保存位置校验失败。";
        NotifyDerivedProperties();
    }

    private void NotifyDerivedProperties()
    {
        OnPropertyChanged(nameof(IsDiscoveryInProgress));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanStartDiscovery));
        OnPropertyChanged(nameof(IsDiscoveryIncomplete));
        OnPropertyChanged(nameof(IsNoSourceFound));
        OnPropertyChanged(nameof(IsSourceInvalid));
        OnPropertyChanged(nameof(IsMultipleSourcesRequireSelection));
        OnPropertyChanged(nameof(HasSelectedSource));
        OnPropertyChanged(nameof(IsAccountListVisible));
        OnPropertyChanged(nameof(CanCreateSnapshot));
        OnPropertyChanged(nameof(StateSummary));
        OnPropertyChanged(nameof(SnapshotLocationSummary));
        OnPropertyChanged(nameof(DetailedDiscoverySummary));
    }

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }
}
