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
        : this(services, marshal: null)
    {
    }

    /// <summary>Test seam: a direct marshaler runs without a UI dispatcher.</summary>
    internal MaterializationViewModel(DesktopServices services, Action<Action>? marshal)
        : base(services, marshal)
    {
        RunHost.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName == nameof(WorkflowRunHost.LastErrorCode))
            {
                OnPropertyChanged(nameof(IsUacRejected));
            }
        };
    }

    /// <summary>True when the last failure was a declined UAC elevation prompt.</summary>
    public bool IsUacRejected => RunHost.LastErrorCode == ErrorCode.UacElevationRejected;

    public override string Title => "物料化";

    public override bool CanNavigate => Services.Project.Snapshot is not null;

    public override string? NavigationHint => CanNavigate ? null : "请先创建源快照";

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

    [RelayCommand]
    private Task MaterializeAsync()
    {
        UacRejected = false;
        var snapshotDirectory = string.IsNullOrWhiteSpace(SnapshotDirectory) ? Services.Project.SnapshotDirectory : SnapshotDirectory;
        var outputDirectory = OutputDirectory;
        var requestedAccount = string.IsNullOrWhiteSpace(RequestedAccount) ? null : RequestedAccount;
        var workspaceOutputPath = string.IsNullOrWhiteSpace(WorkspaceOutputPath) ? null : WorkspaceOutputPath;
        return RunHost.RunAsync(
            CreateConfirmationSession,
        async (context, cancellationToken) =>
        {
            if (Services.Project.Snapshot?.Manifest.PotentiallyInconsistent == true)
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
