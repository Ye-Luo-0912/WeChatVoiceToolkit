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
    private bool _autoResumeAttempted;

    public bool HasContinueSummary => ContinueSummary is not null;

    partial void OnContinueSummaryChanged(string? value)
        => OnPropertyChanged(nameof(HasContinueSummary));

    public override async Task OnNavigatedToAsync(CancellationToken cancellationToken = default)
    {
        await EnsureRecentProjectLoadedAsync(cancellationToken).ConfigureAwait(false);
        await base.OnNavigatedToAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Restores the newest verified project into the application session. This
    /// is callable by the shell before any page is opened, so navigating
    /// directly to Contacts/Scan/Export does not require visiting this page.
    /// </summary>
    public async Task EnsureRecentProjectLoadedAsync(CancellationToken cancellationToken = default)
    {
        await RefreshAsync().ConfigureAwait(false);
        if (!_autoResumeAttempted && Projects.FirstOrDefault(static item => item.CanContinue) is not null)
        {
            _autoResumeAttempted = true;
            await ContinueAsync().ConfigureAwait(false);
        }
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
                ? $"发现 {Projects.Count} 个最近项目；应用会自动继续最近的可验证项目。"
                : "尚未发现可继续的本地项目。请从底部导航选择「微信数据」创建新项目。";
            SelectedProject = Projects.FirstOrDefault(p => p.CanContinue) ?? Projects.FirstOrDefault();
        }).ConfigureAwait(false);
    }

    [RelayCommand]
    private async Task ContinueAsync()
    {
        var selected = SelectedProject ?? Projects.FirstOrDefault(static item => item.CanContinue);
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
                Services.Project.ExportDirectory = selected.LastExportDirectory;
                Services.Project.DatasetOutputDirectory = selected.LastDatasetDirectory;
                RestoreSnapshotReference(result.Workspace);
                Services.Project.ClearVoiceSelection(clearContact: true);
                Services.RecentWorkspaces.Add(result.Workspace, result.WorkspacePath);

                // Rehydrate the last stable contact and scan query. The
                // workflow verifies the workspace first; contact/scan then
                // reuse their persistent cache when the local data is
                // unchanged. No user selection or key acquisition is needed.
                if (!string.IsNullOrWhiteSpace(selected.LastContactUsername))
                {
                    var contactResult = await Workflows.ContactDiscovery.RunAsync(
                        new ContactDiscoveryRequest(result.WorkspacePath, Username: selected.LastContactUsername),
                        new WorkflowContext(Services.Workflows.AccountConfirmation),
                        cancellationToken).ConfigureAwait(false);
                    var contact = contactResult.Contacts.SingleOrDefault(item =>
                        string.Equals(item.Username, selected.LastContactUsername, StringComparison.Ordinal)
                        && (string.IsNullOrWhiteSpace(selected.LastContactId)
                            || string.Equals(item.ContactId, selected.LastContactId, StringComparison.Ordinal)));
                    if (contact is not null)
                    {
                        Services.Project.SelectedContact = contact;
                        var scanQuery = selected.LastScanQuery;
                        if (scanQuery is not null)
                        {
                            var scanResult = await Workflows.VoiceScan.RunAsync(
                                new VoiceScanWorkflowRequest(
                                    result.WorkspacePath,
                                    contact.Username,
                                    Direction: ParseDirection(scanQuery.Direction),
                                    From: ParseUtc(scanQuery.FromUtc),
                                    To: ParseUtc(scanQuery.ToUtc),
                                    MaximumResults: scanQuery.MaximumResults,
                                    DeepScan: scanQuery.DeepScan,
                                    ResolveDurations: scanQuery.ResolveDurations,
                                    ExpectedContactId: contact.ContactId,
                                    MinimumDurationMs: scanQuery.MinimumDurationMs,
                                    MaximumDurationMs: scanQuery.MaximumDurationMs,
                                    MinimumPayloadBytes: scanQuery.MinimumPayloadBytes,
                                    MaximumPayloadBytes: scanQuery.MaximumPayloadBytes),
                                new WorkflowContext(Services.Workflows.AccountConfirmation),
                                cancellationToken).ConfigureAwait(false);
                            Services.Project.Scan = scanResult;
                            Services.Project.SelectionPlan = scanResult.Selection;
                        }
                    }
                }
            }).ConfigureAwait(false);

        if (RunHost.LastErrorCode is null)
        {
            ContinueSummary = $"已自动继续最近项目：已复用 Workspace、联系人、扫描结果和输出目录。";
            NavigateToLastPage(selected.LastPage);
        }
    }

    private void RestoreSnapshotReference(VerifiedLocalWorkspace workspace)
    {
        var snapshotId = workspace.Workspace.Provenance?.SourceSnapshotId;
        if (string.IsNullOrWhiteSpace(snapshotId))
        {
            return;
        }

        var recent = Services.RecentWorkspaces.FindSnapshotById(snapshotId);
        if (recent is null)
        {
            return;
        }

        var manifestPath = Path.Combine(recent.SnapshotDirectory, ".wechatvoice", "snapshot-manifest.json");
        try
        {
            if (!File.Exists(manifestPath))
            {
                return;
            }

            var manifest = System.Text.Json.JsonSerializer.Deserialize<SnapshotManifest>(File.ReadAllText(manifestPath));
            if (manifest is null
                || !string.Equals(manifest.SnapshotId, snapshotId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetFullPath(manifest.SnapshotDirectory), Path.GetFullPath(recent.SnapshotDirectory), StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Services.Project.Snapshot = new SnapshotWorkflowResult(
                manifest,
                SnapshotSourceIdentity.TryDerive(manifest.SourceDirectory, manifest.Files),
                manifestPath);
            Services.Project.SnapshotDirectory = recent.SnapshotDirectory;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            // Workspace reuse remains valid even if the optional historical
            // snapshot index is unavailable.
        }
    }

    private static VoiceDirection? ParseDirection(string? value)
        => Enum.TryParse<VoiceDirection>(value, true, out var direction) ? direction : VoiceDirection.Incoming;

    private static DateTimeOffset? ParseUtc(string? value)
        => DateTimeOffset.TryParse(value, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed.ToUniversalTime()
            : null;

    private void NavigateToLastPage(string? pageId)
    {
        var target = pageId switch
        {
            nameof(ContactViewModel) => typeof(ContactViewModel),
            nameof(ScanViewModel) => typeof(ScanViewModel),
            nameof(ExportViewModel) => typeof(ExportViewModel),
            nameof(DatasetCurationViewModel) => typeof(DatasetCurationViewModel),
            _ => null,
        };
        if (target is not null)
        {
            Services.Navigation.NavigateTo(target);
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
/// A single inspect result for the resume list. The newest valid entry is
/// resumed automatically; the list is informational and never a required
/// selection step.
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
        LastContactUsername = entry.LastContactUsername;
        LastContactId = entry.LastContactId;
        LastScanQuery = entry.LastScanQuery;
        LastDatasetDirectory = entry.LastDatasetDirectory;
        LastPage = entry.LastPage;
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
    public string? LastContactUsername { get; }
    public string? LastContactId { get; }
    public RecentScanQuery? LastScanQuery { get; }
    public string? LastDatasetDirectory { get; }
    public string? LastPage { get; }

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
