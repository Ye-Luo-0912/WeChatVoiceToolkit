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
    private readonly DialogAccountConfirmation _confirmation = new();

    public MaterializationViewModel(DesktopServices services)
        : this(services, marshal: null)
    {
    }

    /// <summary>Test seam: a direct marshaler runs without a UI dispatcher.</summary>
    internal MaterializationViewModel(DesktopServices services, Action<Action>? marshal)
        : base(services, marshal)
    {
        _confirmation.ConfirmationRequested += (_, report) =>
        {
            PendingAccountCandidate = report.AccountCandidate;
            IsConfirmDialogOpen = true;
        };
        RunHost.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName == nameof(WorkflowRunHost.LastError))
            {
                OnPropertyChanged(nameof(IsUacRejected));
            }
        };
    }

    /// <summary>True when the last failure was a declined UAC elevation prompt.</summary>
    public bool IsUacRejected => RunHost.LastError?.Contains("UacElevationRejected", StringComparison.Ordinal) == true;

    public override string Title => "物料化";

    [ObservableProperty]
    private string? _snapshotDirectory;

    [ObservableProperty]
    private string? _outputDirectory;

    [ObservableProperty]
    private string? _workspaceOutputPath;

    [ObservableProperty]
    private string? _requestedAccount;

    [ObservableProperty]
    private bool _allowDevelopmentBroker;

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
    private Task MaterializeAsync() => RunHost.RunAsync(_confirmation, async (context, cancellationToken) =>
    {
        if (string.IsNullOrWhiteSpace(SnapshotDirectory) || string.IsNullOrWhiteSpace(OutputDirectory))
        {
            throw new ArgumentException("请填写快照目录与输出目录。");
        }

        if (AllowDevelopmentBroker)
        {
            Services.Log.Info("development broker opt-in");
        }

        UacRejected = false;
        var result = await Workflows.Materialization.RunAsync(
            new MaterializationWorkflowRequest(
                SnapshotDirectory,
                SnapshotManifestPath: null,
                BackendId: "weixin-windows-4",
                ExternalDecryptorPath: null,
                AllowUntrustedBackend: false,
                AllowDevelopmentBroker: AllowDevelopmentBroker,
                RequestedAccountId: string.IsNullOrWhiteSpace(RequestedAccount) ? null : RequestedAccount,
                OutputDirectory,
                WorkspaceOutputPath: string.IsNullOrWhiteSpace(WorkspaceOutputPath) ? null : WorkspaceOutputPath),
            context,
            cancellationToken).ConfigureAwait(false);
        Services.RecentWorkspaces.Add(result.Workspace, result.LocalWorkspacePath);
        IdentitySummary = result.AccountIdentity.State == AccountIdentityState.Confirmed
            ? $"账号已确认：{result.Workspace.DataSet.AccountId}（{result.AccountIdentity.ConfirmedBy}）"
            : "账号身份为候选状态";
        ResultSummary = $"物料化完成：Workspace {result.Workspace.Workspace.WorkspaceId}；数据库 {result.Workspace.DataSet.Databases.Count} 个；"
            + (result.ProfileId is null ? "外部后端" : $"Profile {result.ProfileId} / MaterializationId {result.MaterializationId}");
    });

    /// <summary>User confirmed the detected account in the dialog.</summary>
    [RelayCommand]
    private void ConfirmAccount()
    {
        IsConfirmDialogOpen = false;
        _confirmation.Complete(confirmed: true, PendingAccountCandidate);
    }

    /// <summary>User declined the detected account; the run fails with AccountConfirmationRequired.</summary>
    [RelayCommand]
    private void DeclineAccount()
    {
        IsConfirmDialogOpen = false;
        _confirmation.Complete(confirmed: false, null);
    }
}
