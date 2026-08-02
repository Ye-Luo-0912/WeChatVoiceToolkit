using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WeChatVoice.Core.Errors;
using WeChatVoice.Core.Models;
using WeChatVoice.Desktop.Infrastructure;
using WeChatVoice.Workflows.Workflows;

namespace WeChatVoice.Desktop.ViewModels;

/// <summary>
/// Materialization page. Runs the one-shot elevated Key Broker (UAC prompt;
/// declines surface as <see cref="ErrorCode.UacElevationRejected"/>) and
/// confirms the detected account before any privileged work. The produced
/// verified workspace is recorded in the recent-workspaces store.
/// </summary>
public sealed partial class MaterializationViewModel : PageViewModelBase
{
    private DialogAccountConfirmation? _activeConfirmation;

    public MaterializationViewModel(DesktopServices services)
        : this(services, invokeOnUi: null)
    {
    }

    /// <summary>Test seam: an awaitable UI dispatcher runs without Avalonia.</summary>
    internal MaterializationViewModel(DesktopServices services, Func<Action, Task>? invokeOnUi)
        : base(services, invokeOnUi)
    {
        RunHost.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName == nameof(WorkflowRunHost.LastErrorCode))
            {
                OnPropertyChanged(nameof(IsUacRejected));
            }

            if (eventArgs.PropertyName is nameof(WorkflowRunHost.State)
                or nameof(WorkflowRunHost.IsRunning))
            {
                if (!RunHost.IsAwaitingUser)
                {
                    IsConfirmDialogOpen = false;
                    PendingAccountCandidate = null;
                }

                if (!RunHost.IsRunning)
                {
                    _activeConfirmation = null;
                }
            }
        };
    }

    /// <summary>True when the last failure was a declined UAC elevation prompt.</summary>
    public bool IsUacRejected => RunHost.LastErrorCode == ErrorCode.UacElevationRejected;

    public override string Title => "物料化";

    public override bool CanNavigate
        => Services.Project.Snapshot is not null
            && Services.Project.EnvironmentAssessment?.BrokerAcquireAndMaterializeAvailable == true;

    public override string? NavigationHint => CanNavigate ? null
        : Services.Project.Snapshot is null
            ? "请先创建源快照"
            : Services.Project.EnvironmentAssessment is null
                ? "请先完成环境检测"
                : "环境检测未通过 Broker/Worker 信任预检";

    [ObservableProperty]
    private string? _snapshotDirectory;

    [ObservableProperty]
    private string? _outputDirectory;

    [ObservableProperty]
    private string? _workspaceOutputPath;

    [ObservableProperty]
    private string? _requestedAccount;

    [ObservableProperty]
    private string? _resultSummary;

    [ObservableProperty]
    private string? _identitySummary;

    [ObservableProperty]
    private bool _uacRejected;

    [ObservableProperty]
    private string? _pendingAccountCandidate;

    [ObservableProperty]
    private bool _isConfirmDialogOpen;

    protected override void OnProjectPropertyChanged(string? propertyName)
    {
        if (propertyName == nameof(ExportProjectSession.SnapshotDirectory))
        {
            SnapshotDirectory = Services.Project.SnapshotDirectory;
        }
    }

    [RelayCommand]
    private async Task BrowseSnapshotAsync()
    {
        var path = await Services.FolderPicker.PickFolderAsync("选择已验证快照目录").ConfigureAwait(true);
        if (path is not null)
        {
            SnapshotDirectory = path;
        }
    }

    [RelayCommand]
    private async Task BrowseOutputAsync()
    {
        var path = await Services.FolderPicker.PickFolderAsync("选择明文 Workspace 输出目录").ConfigureAwait(true);
        if (path is not null)
        {
            OutputDirectory = path;
        }
    }

    [RelayCommand]
    private Task MaterializeAsync()
    {
        UacRejected = false;
        var snapshot = Services.Project.Snapshot;
        var snapshotDirectory = string.IsNullOrWhiteSpace(SnapshotDirectory) ? Services.Project.SnapshotDirectory : SnapshotDirectory;
        var outputDirectory = OutputDirectory;
        var requestedAccount = string.IsNullOrWhiteSpace(RequestedAccount) ? null : RequestedAccount;
        var workspaceOutputPath = string.IsNullOrWhiteSpace(WorkspaceOutputPath) ? null : WorkspaceOutputPath;
        var environment = Services.Project.EnvironmentAssessment;
        return RunHost.RunAsync(
            CreateConfirmationSession,
        async (context, cancellationToken) =>
        {
            if (environment is null)
            {
                throw new AppFailureException(
                    ErrorCode.InvalidRequest,
                    "请先完成环境检测，再开始物料化。 ");
            }

            if (!environment.BrokerAcquireAndMaterializeAvailable)
            {
                throw new AppFailureException(
                    ErrorCode.WorkerBundleUntrusted,
                    "环境检测中的 Broker、Worker 或安装目录信任校验未通过；请修复环境后重新检测。 ");
            }

            if (snapshot?.Manifest.PotentiallyInconsistent == true)
            {
                throw new AppFailureException(ErrorCode.SnapshotInvalid, "此快照来自活动源，不可用于解密或导出。");
            }
            if (string.IsNullOrWhiteSpace(snapshotDirectory) || string.IsNullOrWhiteSpace(outputDirectory))
            {
                throw new AppFailureException(WeChatVoice.Core.Errors.ErrorCode.InvalidRequest, "Snapshot and output directories are required.");
            }

            return await Workflows.Materialization.RunAsync(
                new MaterializationWorkflowRequest(
                    snapshotDirectory,
                    SnapshotManifestPath: null,
                    BackendId: "weixin-windows-4",
                    ExternalDecryptorPath: null,
                    AllowUntrustedBackend: false,
                    RequestedAccountId: requestedAccount,
                    outputDirectory,
                    WorkspaceOutputPath: workspaceOutputPath),
                context,
                cancellationToken).ConfigureAwait(false);
        },
            result =>
            {
                Services.Project.Materialization = result;
                Services.Project.ClearVoiceSelection(clearContact: true);
                Services.Project.Workspace = result.Workspace;
                Services.Project.WorkspacePath = result.LocalWorkspacePath;
                Services.RecentWorkspaces.Add(result.Workspace, result.LocalWorkspacePath);
                IdentitySummary = result.AccountIdentity.State == AccountIdentityState.Confirmed
                    ? $"数据库证据已确认账号：{result.Workspace.DataSet.AccountId}（{result.AccountIdentity.ConfirmedBy}）"
                    : result.AccountIdentity.UserConfirmation == UserConfirmationState.Confirmed
                        ? $"用户已确认账号候选：{result.Workspace.DataSet.AccountId}（证据等级：{result.AccountIdentity.State}）"
                        : $"账号身份为候选状态（证据等级：{result.AccountIdentity.State}）";
                ResultSummary = $"物料化完成：Workspace {result.Workspace.Workspace.WorkspaceId}；数据库 {result.Workspace.DataSet.Databases.Count} 个；"
                    + (result.ProfileId is null ? "外部后端" : $"Profile {result.ProfileId} / MaterializationId {result.MaterializationId}");
            });
    }

    private DialogAccountConfirmation CreateConfirmationSession()
    {
        var confirmation = CreateAccountConfirmation();
        confirmation.ConfirmationRequested += (_, report) =>
        {
            PendingAccountCandidate = report.AccountCandidate;
            IsConfirmDialogOpen = true;
        };
        _activeConfirmation = confirmation;
        return confirmation;
    }

    /// <summary>User confirmed the detected account in the dialog.</summary>
    [RelayCommand]
    private void ConfirmAccount()
    {
        IsConfirmDialogOpen = false;
        _activeConfirmation?.Complete(confirmed: true, PendingAccountCandidate);
    }

    /// <summary>User declined the detected account; the run fails with AccountConfirmationRequired.</summary>
    [RelayCommand]
    private void DeclineAccount()
    {
        IsConfirmDialogOpen = false;
        _activeConfirmation?.Complete(confirmed: false, null);
    }
}
