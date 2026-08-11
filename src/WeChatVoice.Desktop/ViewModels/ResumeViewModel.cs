using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WeChatVoice.Core.Models;
using WeChatVoice.Desktop.Infrastructure;
using WeChatVoice.Workflows.Workflows;

namespace WeChatVoice.Desktop.ViewModels;

/// <summary>
/// Resume-first home page. On activation it inspects the recently used
/// workspaces (via the shared <see cref="IProjectStateWorkflow"/>) and lets the
/// user continue an existing project without re-running Snapshot, UAC, or
/// materialization. The page only presents the verified classification and the
/// user's continue choice; it never re-implements the reuse/recover decision.
/// </summary>
public sealed partial class ResumeViewModel : PageViewModelBase
{
    public ResumeViewModel(DesktopServices services)
        : base(services)
    {
    }

    public override string Title => "继续上次工作";

    /// <summary>
    /// The five distinct refresh semantics. Rendering them as separate actions
    /// keeps the user from treating "continue" and "re-run everything" the same.
    /// </summary>
    public IReadOnlyList<RefreshAction> RefreshActions => RefreshActionCatalog.All;

    public ObservableCollection<ProjectResumeEntryViewModel> Projects { get; } = [];

    [ObservableProperty]
    private ProjectResumeEntryViewModel? _selectedProject;

    [ObservableProperty]
    private string _resumeSummary = "尚未检查本地项目状态。";

    [ObservableProperty]
    private bool _hasProjects;

    [ObservableProperty]
    private string? _continueSummary;

    public bool HasContinueSummary => ContinueSummary is not null;

    partial void OnContinueSummaryChanged(string? value)
        => OnPropertyChanged(nameof(HasContinueSummary));

    public override async Task OnNavigatedToAsync(CancellationToken cancellationToken = default)
    {
        await RefreshAsync().ConfigureAwait(false);
        await base.OnNavigatedToAsync(cancellationToken).ConfigureAwait(false);
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        var entries = Services.RecentWorkspaces.Load();
        var items = new List<ProjectResumeEntryViewModel>();
        foreach (var entry in entries)
        {
            ProjectStageStatus status;
            try
            {
                var context = new WorkflowContext(Services.Workflows.AccountConfirmation);
                status = await Workflows.ProjectState.InspectAsync(
                    new ProjectStateInspectRequest(entry.WorkspacePath),
                    context,
                    CancellationToken.None).ConfigureAwait(true);
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                status = new ProjectStageStatus(
                    ProjectStageState.Invalid,
                    entry.WorkspacePath,
                    null,
                    entry.AccountId,
                    "本地状态无法读取或校验。",
                    RequiresElevation: false,
                    ProducesNewDiskData: true);
            }

            items.Add(new ProjectResumeEntryViewModel(status, entry));
        }

        await InvokeOnUi(() =>
        {
            Projects.Clear();
            foreach (var item in items)
            {
                Projects.Add(item);
            }

            HasProjects = Projects.Count > 0;
            ResumeSummary = HasProjects
                ? $"发现 {Projects.Count} 个最近项目，可选择继续或从源刷新。"
                : "尚未发现可继续的本地项目。请从底部导航选择「微信数据」创建新项目。";
            SelectedProject = Projects.FirstOrDefault(p => p.CanContinue) ?? Projects.FirstOrDefault();
        }).ConfigureAwait(false);
    }

    [RelayCommand]
    private async Task ContinueAsync()
    {
        var selected = SelectedProject;
        if (selected is null)
        {
            return;
        }

        ContinueSummary = null;
        await RunHost.RunAsync(
            async (context, cancellationToken) =>
            {
                var result = await Workflows.ProjectState.ResumeAsync(
                    new ProjectStateResumeRequest(selected.WorkspacePath, AutoRecover: true),
                    context,
                    cancellationToken).ConfigureAwait(false);

                Services.Project.Workspace = result.Workspace;
                Services.Project.WorkspacePath = result.WorkspacePath;
                Services.Project.ClearVoiceSelection(clearContact: true);
                Services.RecentWorkspaces.Add(result.Workspace, result.WorkspacePath);
            }).ConfigureAwait(false);

        if (RunHost.LastErrorCode is null)
        {
            ContinueSummary = $"已继续项目：{selected.WorkspacePath}\n现在可以从左侧导航进入「联系人 / 扫描 / 导出」继续整理。";
        }
    }

    [RelayCommand]
    private void RefreshFromSource()
    {
        Services.Navigation.NavigateTo(typeof(SourceSnapshotViewModel));
    }

    /// <summary>
    /// Routes a refresh action card to the page that owns that workflow. The
    /// <see cref="IProjectStateWorkflow"/> decision stays authoritative; this
    /// only moves the user to the right page for the chosen semantic.
    /// </summary>
    [RelayCommand]
    private void NavigateToAction(RefreshAction? action)
    {
        if (action is null)
        {
            return;
        }

        var pageType = action.Target switch
        {
            RefreshActionCatalog.RefreshSourceId => typeof(SourceSnapshotViewModel),
            RefreshActionCatalog.ReScanId or RefreshActionCatalog.ReAnalyzeId => typeof(ScanViewModel),
            RefreshActionCatalog.RebuildDatasetId => typeof(DatasetCurationViewModel),
            _ => typeof(ResumeViewModel),
        };
        Services.Navigation.NavigateTo(pageType);
    }
}

/// <summary>
/// A single inspect result for the resume list. The row is immutable after
/// inspection; the user picks one to continue.
/// </summary>
public sealed partial class ProjectResumeEntryViewModel : ObservableObject
{
    public ProjectResumeEntryViewModel(ProjectStageStatus status, RecentWorkspaceEntry entry)
    {
        Status = status;
        WorkspacePath = status.WorkspacePath ?? entry.WorkspacePath;
        WorkspaceId = entry.WorkspaceId;
        AccountId = status.AccountId ?? entry.AccountId;
        LastUsedUtc = entry.LastUsedUtc;
        LastExportDirectory = entry.LastExportDirectory;
        State = status.State;
        Reason = status.Reason;
        CanContinue = State is ProjectStageState.ValidReusable or ProjectStageState.Recoverable;
    }

    public ProjectStageStatus Status { get; }

    public string WorkspacePath { get; }

    public string WorkspaceId { get; }

    public string? AccountId { get; }

    public DateTimeOffset LastUsedUtc { get; }

    public string? LastExportDirectory { get; }

    public ProjectStageState State { get; }

    public string? Reason { get; }

    [ObservableProperty]
    private bool _isSelected;

    public bool CanContinue { get; }

    public bool HasReason => Reason is not null;

    public string StateColor => State switch
    {
        ProjectStageState.ValidReusable => "#22C55E",
        ProjectStageState.Recoverable => "#F59E0B",
        ProjectStageState.Busy => "#60A5FA",
        ProjectStageState.Stale => "#F97316",
        ProjectStageState.Invalid => "#EF4444",
        _ => "#64748B",
    };

    public string StateText => State switch
    {
        ProjectStageState.ValidReusable => "可继续（已验证）",
        ProjectStageState.Recoverable => "可恢复（未提交）",
        ProjectStageState.Busy => "进行中",
        ProjectStageState.Stale => "已过期（数据变化）",
        ProjectStageState.Invalid => "需重建",
        _ => "缺失",
    };
}