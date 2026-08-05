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
    private readonly WorkflowRunHost _recoveryAssessmentHost;
    private bool _applyingDefaults;
    private string? _automaticOutputDirectory;
    private string? _automaticWorkspaceOutputPath;

    public MaterializationViewModel(DesktopServices services)
        : this(services, invokeOnUi: null)
    {
    }

    /// <summary>Test seam: an awaitable UI dispatcher runs without Avalonia.</summary>
    internal MaterializationViewModel(DesktopServices services, Func<Action, Task>? invokeOnUi)
        : base(services, invokeOnUi)
    {
        _recoveryAssessmentHost = new WorkflowRunHost(
            invokeOnUi: InvokeOnUi,
            log: services.Log,
            coordinator: services.OperationCoordinator);
        RunHost.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName == nameof(WorkflowRunHost.LastErrorCode))
            {
                OnPropertyChanged(nameof(IsUacRejected));
            }

            if (eventArgs.PropertyName is nameof(WorkflowRunHost.State)
                or nameof(WorkflowRunHost.IsRunning))
            {
                OnPropertyChanged(nameof(MaterializationReadinessSummary));

                if (!RunHost.IsAwaitingUser)
                {
                    IsConfirmDialogOpen = false;
                    PendingAccountCandidate = null;
                }

                if (!RunHost.IsRunning)
                {
                    ClearConfirmationState();
                }
            }
        };
        RefreshWeixinState();
    }

    /// <summary>True when the last failure was a declined UAC elevation prompt.</summary>
    public bool IsUacRejected => RunHost.LastErrorCode == ErrorCode.UacElevationRejected;

    public bool CanStartMaterialization
        => CanStartOperation
            && Services.Project.EnvironmentAssessment?.BrokerAcquireAndMaterializeAvailable == true
            && !string.IsNullOrWhiteSpace(SnapshotDirectory)
            && !string.IsNullOrWhiteSpace(OutputDirectory)
            && IsWeixinProcessReady;

    public string MaterializationReadinessSummary
        => IsConfirmDialogOpen || RunHost.IsAwaitingUser
            ? "已暂停等待账号确认：请在上方确认账号，确认后才会弹出 UAC 并继续。"
            : !IsWeixinProcessReady
            ? "请先启动 Weixin，再点击“刷新 Weixin 状态”。创建快照时需要退出 Weixin，但提取密钥和物料化时必须让 Weixin 保持运行。"
            : !CanStartMaterialization
            ? Services.Project.Snapshot is null
                ? "请先创建源快照。"
                : Services.Project.EnvironmentAssessment?.BrokerAcquireAndMaterializeAvailable != true
                    ? "环境检测尚未通过 Broker/Worker 信任校验。"
                    : "正在准备物料化路径……"
            : "路径已自动准备，可以开始物料化。";

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
    private bool _canRecoverMaterialization;

    [ObservableProperty]
    private string? _recoverySummary;

    [ObservableProperty]
    private string? _pendingAccountCandidate;

    [ObservableProperty]
    private bool _isConfirmDialogOpen;

    [ObservableProperty]
    private bool _isWeixinProcessReady;

    [ObservableProperty]
    private string _weixinProcessSummary = "尚未检查 Weixin 运行状态";

    partial void OnIsConfirmDialogOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(MaterializationReadinessSummary));
    }

    partial void OnIsWeixinProcessReadyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStartMaterialization));
        OnPropertyChanged(nameof(MaterializationReadinessSummary));
    }

    protected override void OnProjectPropertyChanged(string? propertyName)
    {
        switch (propertyName)
        {
            case nameof(ExportProjectSession.SnapshotDirectory):
            case nameof(ExportProjectSession.Snapshot):
            case nameof(ExportProjectSession.EnvironmentAssessment):
                ApplyProjectDefaults();
                OnPropertyChanged(nameof(CanNavigate));
                OnPropertyChanged(nameof(NavigationHint));
                OnPropertyChanged(nameof(CanStartMaterialization));
                OnPropertyChanged(nameof(MaterializationReadinessSummary));
                break;
        }
    }

    partial void OnSnapshotDirectoryChanged(string? value)
    {
        if (!_applyingDefaults && !string.IsNullOrWhiteSpace(value))
        {
            ApplyOutputDefaults(value);
        }

        OnPropertyChanged(nameof(CanStartMaterialization));
        OnPropertyChanged(nameof(MaterializationReadinessSummary));
    }

    partial void OnOutputDirectoryChanged(string? value)
    {
        if (!_applyingDefaults)
        {
            _automaticOutputDirectory = null;
            _automaticWorkspaceOutputPath = null;
        }

        OnPropertyChanged(nameof(CanStartMaterialization));
        OnPropertyChanged(nameof(MaterializationReadinessSummary));
    }

    partial void OnWorkspaceOutputPathChanged(string? value)
    {
        if (!_applyingDefaults)
        {
            _automaticWorkspaceOutputPath = null;
        }
    }

    public override Task OnNavigatedToAsync(CancellationToken cancellationToken = default)
    {
        ApplyProjectDefaults();
        RefreshWeixinState();
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void RefreshWeixinState()
    {
        var processes = Services.WeixinProcessProbe.ListRunning();
        IsWeixinProcessReady = processes.Count > 0;
        WeixinProcessSummary = IsWeixinProcessReady
            ? $"已检测到 Weixin（{processes.Count} 个进程），可以提取密钥。"
            : "未检测到运行中的 Weixin。请启动 Weixin 后刷新状态。";
    }

    private void ApplyProjectDefaults()
    {
        _applyingDefaults = true;
        try
        {
            var projectSnapshotDirectory = Services.Project.SnapshotDirectory;
            if (!string.IsNullOrWhiteSpace(projectSnapshotDirectory))
            {
                SnapshotDirectory = projectSnapshotDirectory;
            }

            if (!string.IsNullOrWhiteSpace(SnapshotDirectory))
            {
                ApplyOutputDefaults(SnapshotDirectory);
            }
        }
        finally
        {
            _applyingDefaults = false;
        }

        OnPropertyChanged(nameof(CanNavigate));
        OnPropertyChanged(nameof(NavigationHint));
        OnPropertyChanged(nameof(CanStartMaterialization));
        OnPropertyChanged(nameof(MaterializationReadinessSummary));
    }

    private void ApplyOutputDefaults(string snapshotDirectory)
    {
        if (string.IsNullOrWhiteSpace(OutputDirectory)
            || string.Equals(OutputDirectory, _automaticOutputDirectory, StringComparison.OrdinalIgnoreCase))
        {
            var output = Services.WorkspaceOutputDirectories.CreateDefault(
                snapshotDirectory,
                Services.Project.Snapshot?.Manifest.SnapshotId);
            OutputDirectory = output;
            _automaticOutputDirectory = output;
        }

        if (string.IsNullOrWhiteSpace(WorkspaceOutputPath)
            || string.Equals(WorkspaceOutputPath, _automaticWorkspaceOutputPath, StringComparison.OrdinalIgnoreCase))
        {
            var workspacePath = WorkspaceOutputDirectoryFactory.CreateWorkspaceDocumentPath(OutputDirectory!);
            WorkspaceOutputPath = workspacePath;
            _automaticWorkspaceOutputPath = workspacePath;
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
    private async Task MaterializeAsync()
    {
        UacRejected = false;
        CanRecoverMaterialization = false;
        RecoverySummary = null;
        var snapshot = Services.Project.Snapshot;
        var snapshotDirectory = string.IsNullOrWhiteSpace(SnapshotDirectory) ? Services.Project.SnapshotDirectory : SnapshotDirectory;
        var outputDirectory = OutputDirectory;
        var requestedAccount = string.IsNullOrWhiteSpace(RequestedAccount) ? null : RequestedAccount;
        var workspaceOutputPath = string.IsNullOrWhiteSpace(WorkspaceOutputPath) ? null : WorkspaceOutputPath;
        var environment = Services.Project.EnvironmentAssessment;
        RefreshWeixinState();
        await RunHost.RunAsync(
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

            if (Services.WeixinProcessProbe.ListRunning().Count == 0)
            {
                throw new AppFailureException(
                    ErrorCode.WeixinNotRunning,
                    "请启动 Weixin 后再执行物料化；密钥提取需要读取受控的 Weixin 进程内存。 ");
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
                ApplyMaterializationResult(result);
            });

        if (RunHost.State == WorkflowState.Failed
            && !string.IsNullOrWhiteSpace(outputDirectory)
            && Directory.Exists(outputDirectory))
        {
            await AssessRecoveryAsync(outputDirectory, workspaceOutputPath).ConfigureAwait(true);
        }

        OnPropertyChanged(nameof(CanStartMaterialization));
    }

    [RelayCommand]
    private async Task RecoverMaterializationAsync()
    {
        var outputDirectory = OutputDirectory;
        var workspaceOutputPath = string.IsNullOrWhiteSpace(WorkspaceOutputPath) ? null : WorkspaceOutputPath;
        if (!CanRecoverMaterialization || string.IsNullOrWhiteSpace(outputDirectory))
        {
            return;
        }

        CanRecoverMaterialization = false;
        await RunHost.RunAsync(
            CreateConfirmationSession,
            async (context, cancellationToken) => await Workflows.Workspace.RecoverMaterializationAsync(
                new MaterializationRecoveryRequest(outputDirectory, workspaceOutputPath, RequestedAccount),
                context,
                cancellationToken).ConfigureAwait(false),
            result => ApplyRecoveredWorkspace(result, outputDirectory, workspaceOutputPath));

        if (RunHost.State == WorkflowState.Failed && Directory.Exists(outputDirectory))
        {
            await AssessRecoveryAsync(outputDirectory, workspaceOutputPath).ConfigureAwait(true);
        }
    }

    private async Task AssessRecoveryAsync(string outputDirectory, string? workspaceOutputPath)
    {
        await _recoveryAssessmentHost.RunAsync(
            (context, cancellationToken) => Workflows.Workspace.AssessMaterializationRecoveryAsync(
                outputDirectory,
                workspaceOutputPath,
                context,
                cancellationToken),
            assessment =>
            {
                CanRecoverMaterialization = assessment.CanRecover;
                RecoverySummary = assessment.CanRecover
                    ? $"检测到可恢复物料化状态：{assessment.State}。可恢复 Workspace，不重新解密。"
                    : assessment.State is null
                        ? null
                        : $"当前物料化状态：{assessment.State}，暂不可恢复。";
            });
    }

    private void ApplyMaterializationResult(MaterializationWorkflowResult result)
    {
        Services.Project.Materialization = result;
        Services.Project.ClearVoiceSelection(clearContact: true);
        Services.Project.Workspace = result.Workspace;
        Services.Project.WorkspacePath = result.LocalWorkspacePath;
        Services.RecentWorkspaces.Add(result.Workspace, result.LocalWorkspacePath);
        CanRecoverMaterialization = false;
        RecoverySummary = null;
        IdentitySummary = result.AccountIdentity.State == AccountIdentityState.Confirmed
            ? $"数据库证据已确认账号：{result.Workspace.DataSet.AccountId}（{result.AccountIdentity.ConfirmedBy}）"
            : result.AccountIdentity.UserConfirmation == UserConfirmationState.Confirmed
                ? $"用户已确认账号候选：{result.Workspace.DataSet.AccountId}（证据等级：{result.AccountIdentity.State}）"
                : $"账号身份为候选状态（证据等级：{result.AccountIdentity.State}）";
        ResultSummary = $"物料化完成：Workspace {result.Workspace.Workspace.WorkspaceId}；数据库 {result.Workspace.DataSet.Databases.Count} 个；"
            + (result.ProfileId is null ? "外部后端" : $"Profile {result.ProfileId} / MaterializationId {result.MaterializationId}");
    }

    private void ApplyRecoveredWorkspace(
        VerifiedLocalWorkspace workspace,
        string outputDirectory,
        string? workspaceOutputPath)
    {
        var path = Path.GetFullPath(workspaceOutputPath ?? Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(outputDirectory))!,
            Path.GetFileName(Path.GetFullPath(outputDirectory)) + ".workspace.json"));
        Services.Project.Materialization = null;
        Services.Project.ClearVoiceSelection(clearContact: true);
        Services.Project.Workspace = workspace;
        Services.Project.WorkspacePath = path;
        Services.RecentWorkspaces.Add(workspace, path);
        CanRecoverMaterialization = false;
        RecoverySummary = "Workspace 已恢复；数据库未重新解密。";
        ResultSummary = $"恢复完成：Workspace {workspace.Workspace.WorkspaceId}；数据库 {workspace.DataSet.Databases.Count} 个。";
        IdentitySummary = workspace.AccountIdentity.UserConfirmation == UserConfirmationState.Confirmed
            ? $"用户已确认账号候选：{workspace.DataSet.AccountId}（证据等级：{workspace.AccountIdentity.State}）"
            : $"账号身份为候选状态（证据等级：{workspace.AccountIdentity.State}）";
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
        var accountId = PendingAccountCandidate;
        IsConfirmDialogOpen = false;
        PendingAccountCandidate = null;
        _activeConfirmation?.Complete(confirmed: true, accountId);
    }

    /// <summary>User declined the detected account; the run fails with AccountConfirmationRequired.</summary>
    [RelayCommand]
    private void DeclineAccount()
    {
        IsConfirmDialogOpen = false;
        PendingAccountCandidate = null;
        _activeConfirmation?.Complete(confirmed: false, null);
    }

    private void ClearConfirmationState()
    {
        IsConfirmDialogOpen = false;
        PendingAccountCandidate = null;
        _activeConfirmation = null;
    }
}
