using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WeChatVoice.Core.Errors;
using WeChatVoice.Core.Models;
using WeChatVoice.Desktop.Infrastructure;
using WeChatVoice.Workflows.Workflows;

namespace WeChatVoice.Desktop.ViewModels;

/// <summary>
/// Export page: raw SILK only (no decode), exactly one 1:1 contact, one
/// streaming read per source BLOB. Supports cancel and retry; the run journal
/// makes partial failures visible and repeat runs skip hash-verified files.
/// </summary>
public sealed partial class ExportViewModel : PageViewModelBase
{
    private string? _automaticOutputDirectory;

    public ExportViewModel(DesktopServices services)
        : this(services, invokeOnUi: null)
    {
    }

    /// <summary>Test seam: an awaitable UI dispatcher runs without Avalonia.</summary>
    internal ExportViewModel(DesktopServices services, Func<Action, Task>? invokeOnUi)
        : base(services, invokeOnUi)
    {
        WorkspacePath = services.Project.WorkspacePath;
        RunHost.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(WorkflowRunHost.State) or nameof(WorkflowRunHost.IsRunning))
            {
                OnPropertyChanged(nameof(CanExport));
            }
        };
    }

    public override string Title => "语音导出";

    public override bool CanNavigate
        => Services.Project.Workspace is not null
            && Services.Project.SelectedContact is not null
            && Services.Project.Scan is not null
            && Services.Project.SelectionPlan is { ScanReport.ExportableVoiceCount: > 0 };

    public override string? NavigationHint => CanNavigate ? null : Services.Project.Workspace is null
        ? "请先完成物料化或加载 Workspace"
        : Services.Project.SelectedContact is null
            ? "请先在联系人页显式选择联系人"
            : "请先完成包含可导出语音的扫描";

    public bool CanExport => CanStartOperation && CanNavigate;

    public bool HasExportResult => !string.IsNullOrWhiteSpace(ExportSummary);

    public bool HasFailures => Failures.Count > 0;

    public string OutputDirectorySummary
        => string.IsNullOrWhiteSpace(OutputDirectory)
            ? "将自动保存到应用数据目录"
            : $"自动保存位置：{OutputDirectory}";

    public override Task OnNavigatedToAsync(CancellationToken cancellationToken = default)
    {
        WorkspacePath = Services.Project.WorkspacePath;
        ApplyOutputDefaults();
        return Task.CompletedTask;
    }

    [ObservableProperty]
    private string? _workspacePath;

    [ObservableProperty]
    private string? _contactUsername;

    [ObservableProperty]
    private string? _outputDirectory;

    [ObservableProperty]
    private string? _exportSummary;

    [ObservableProperty]
    private int _exportedCount;

    [ObservableProperty]
    private int _skippedCount;

    [ObservableProperty]
    private int _failureCount;

    [ObservableProperty]
    private IReadOnlyList<VoiceExportFailure> _failures = [];

    [ObservableProperty]
    private string? _manifestPath;

    [ObservableProperty]
    private long _totalTrainingDurationMs;

    [ObservableProperty]
    private int _trainingEntryCount;

    [ObservableProperty]
    private string? _verificationSummary;

    [RelayCommand]
    private Task ExportAsync()
    {
        var workspacePath = string.IsNullOrWhiteSpace(WorkspacePath) ? Services.Project.WorkspacePath : WorkspacePath;
        var outputDirectory = string.IsNullOrWhiteSpace(OutputDirectory)
            ? Services.Project.ExportDirectory
            : OutputDirectory;
        var plan = Services.Project.SelectionPlan;
        var scan = Services.Project.Scan;
        var contact = Services.Project.SelectedContact;
        var workspace = Services.Project.Workspace;
        var sessionWorkspacePath = Services.Project.WorkspacePath;
        return RunHost.RunAsync(
        async (context, cancellationToken) =>
        {
            if (plan is null || scan is null || plan.ScanReport.ExportableVoiceCount == 0 || contact is null || string.IsNullOrWhiteSpace(contact.Username))
            {
                throw new AppFailureException(ErrorCode.InvalidRequest, "Complete contact selection and an exportable scan before exporting.");
            }

            if (workspace is null || !string.Equals(plan.WorkspaceId, workspace.Workspace.WorkspaceId, StringComparison.Ordinal))
            {
                throw new AppFailureException(ErrorCode.InvalidRequest, "The scan plan is no longer valid for this workspace.");
            }

            if (!ReferenceEquals(scan.Report, plan.ScanReport))
            {
                throw new AppFailureException(ErrorCode.InvalidRequest, "The scan result no longer matches the immutable export plan.");
            }

            if (string.IsNullOrWhiteSpace(plan.ResultSetFingerprint))
            {
                throw new AppFailureException(ErrorCode.InvalidRequest, "The scan did not produce a verifiable result-set fingerprint; run the scan again.");
            }

            if (string.IsNullOrWhiteSpace(workspacePath) || string.IsNullOrWhiteSpace(outputDirectory))
            {
                throw new AppFailureException(ErrorCode.InvalidRequest, "导出目录尚未准备好，请返回联系人或扫描页后重新进入导出。 ");
            }

            if (!string.IsNullOrWhiteSpace(sessionWorkspacePath)
                && !PathsEqual(workspacePath, sessionWorkspacePath))
            {
                throw new AppFailureException(ErrorCode.InvalidRequest, "Workspace path changed; run Scan again for the selected Workspace.");
            }

            return await Workflows.VoiceExport.RunAsync(
                plan,
                new ExportDestination(outputDirectory),
                context,
                cancellationToken).ConfigureAwait(false);
        },
        result =>
        {
            if (plan is null || contact is null || !IsCurrentExportSelection(plan, contact, workspacePath))
            {
                throw new AppFailureException(ErrorCode.InvalidRequest, "Contact or scan selection changed while exporting; review the plan and run again.");
            }

            var manifest = result.Manifest;
            ExportedCount = manifest.Entries.Count(static entry => !entry.WasSkipped);
            SkippedCount = manifest.Entries.Count(static entry => entry.WasSkipped);
            TotalTrainingDurationMs = manifest.TotalTrainingDurationMs;
            TrainingEntryCount = manifest.TrainingEntryCount;
            Failures = manifest.Failures;
            FailureCount = manifest.Failures.Count;
            OnPropertyChanged(nameof(HasExportResult));
            OnPropertyChanged(nameof(HasFailures));
            ExportSummary = manifest.RunStatus switch
            {
                ExportRunStatus.Completed => $"导出完成：新增 {ExportedCount} 条，跳过 {SkippedCount} 条",
                ExportRunStatus.CompletedWithFailures => $"导出完成（含失败）：新增 {ExportedCount} 条，失败 {FailureCount} 条",
                ExportRunStatus.Cancelled => $"导出已取消：已完成 {ExportedCount} 条",
                _ => $"导出失败：{FailureCount} 条",
            };

            // ExactAllOrNothing returns a failure manifest for presentation,
            // but it has no committed export to promote into the application
            // project. Keep the previous successful project/export intact so
            // Dataset Curation cannot navigate into an uncommitted run.
            if (manifest.RunStatus == ExportRunStatus.Failed)
            {
                return;
            }

            Services.Project.LastExportRun = result;
            Services.Project.Workspace = result.Workspace;
            Services.Project.ExportDirectory = outputDirectory;
            ManifestPath = string.IsNullOrWhiteSpace(outputDirectory)
                ? null
                : Path.Combine(Path.GetFullPath(outputDirectory), "runs", result.Manifest.RunId + ".dataset.manifest.json");
            if (!string.IsNullOrWhiteSpace(outputDirectory)
                && !string.IsNullOrWhiteSpace(Services.Project.WorkspacePath))
            {
                Services.RecentWorkspaces.SetLastExportDirectory(Services.Project.WorkspacePath, outputDirectory);
                Services.StoragePathRegistry.Register(outputDirectory, StorageAssetKind.UserAsset);
            }
        });
    }

    [RelayCommand]
    private Task VerifyExportAsync()
    {
        var exportDirectory = OutputDirectory ?? Services.Project.ExportDirectory;
        return RunHost.RunAsync(
            async (context, cancellationToken) => await Workflows.VoiceExport.VerifyAsync(
                new ExportVerificationRequest(exportDirectory ?? throw new AppFailureException(ErrorCode.InvalidRequest, "Export directory is required.")),
                context,
                cancellationToken).ConfigureAwait(false),
            result =>
            {
                VerificationSummary = result.IsValid
                    ? $"导出验证通过：{result.VerifiedOriginalCount} 个 SILK，Journal/Csv/训练选择一致。"
                    : $"导出验证失败：{result.Issues.Count} 项；缺失 {result.MissingFileCount}，多余 {result.ExtraFileCount}。请查看错误码后执行修复。";
            });
    }

    [RelayCommand]
    private Task RepairExportAsync()
    {
        var exportDirectory = OutputDirectory ?? Services.Project.ExportDirectory;
        return RunHost.RunAsync(
            async (context, cancellationToken) => await Workflows.VoiceExport.RepairAsync(
                new ExportRepairRequest(exportDirectory ?? throw new AppFailureException(ErrorCode.InvalidRequest, "Export directory is required.")),
                context,
                cancellationToken).ConfigureAwait(false),
            result => VerificationSummary = result.Verification.IsValid
                ? $"导出派生文件已修复并验证：{result.Verification.VerifiedOriginalCount} 个 SILK；原始 SILK 未修改。"
                : $"导出修复后仍未通过验证：{result.Verification.Issues.Count} 项。");
    }

    /// <summary>Recovers a manifest from a flushed run journal after a crash.</summary>
    [RelayCommand]
    private Task RecoverAsync(string? journalPath)
    {
        var capturedJournalPath = journalPath;
        return RunHost.RunAsync(
            async (_, cancellationToken) =>
            {
                if (string.IsNullOrWhiteSpace(capturedJournalPath))
                {
                    throw new AppFailureException(ErrorCode.InvalidRequest, "Journal path is required.");
                }

                return await Workflows.VoiceExport.RecoverRunAsync(capturedJournalPath, cancellationToken).ConfigureAwait(false);
            },
                manifest =>
            {
                ExportedCount = manifest.Entries.Count(static entry => !entry.WasSkipped);
                SkippedCount = manifest.Entries.Count(static entry => entry.WasSkipped);
                TotalTrainingDurationMs = manifest.TotalTrainingDurationMs;
                TrainingEntryCount = manifest.TrainingEntryCount;
                Failures = manifest.Failures;
                ExportSummary = $"Journal 恢复完成：{ExportedCount} 条，失败 {manifest.Failures.Count} 条";
            });
    }

    [ObservableProperty]
    private string? _journalPath;

    partial void OnWorkspacePathChanged(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || string.IsNullOrWhiteSpace(Services.Project.WorkspacePath)
            || PathsEqual(value, Services.Project.WorkspacePath))
        {
            return;
        }

        Services.Project.Scan = null;
        Services.Project.SelectionPlan = null;
        Services.Project.LastExportRun = null;
    }

    partial void OnOutputDirectoryChanged(string? value)
    {
        if (!string.Equals(value, _automaticOutputDirectory, StringComparison.OrdinalIgnoreCase))
        {
            _automaticOutputDirectory = null;
        }

        Services.Project.LastExportRun = null;
        OnPropertyChanged(nameof(OutputDirectorySummary));
    }

    private void ApplyOutputDefaults()
    {
        var workspace = Services.Project.Workspace;
        var contact = Services.Project.SelectedContact;
        if (workspace is null || contact is null)
        {
            return;
        }

        var recent = Services.RecentWorkspaces.Load().FirstOrDefault(entry =>
            string.Equals(entry.WorkspacePath, Services.Project.WorkspacePath, StringComparison.OrdinalIgnoreCase));
        var existing = recent?.LastExportDirectory ?? Services.Project.ExportDirectory;
        if (!string.IsNullOrWhiteSpace(existing))
        {
            OutputDirectory = existing;
            Services.Project.ExportDirectory = existing;
            _automaticOutputDirectory = existing;
            return;
        }

        var output = new ExportOutputDirectoryFactory(Services.RecentWorkspaces.StorageDirectory)
            .CreateDefault(workspace.Workspace.WorkspaceId, contact.ContactId);
        OutputDirectory = output;
        Services.Project.ExportDirectory = output;
        _automaticOutputDirectory = output;
    }

    [RelayCommand]
    private async Task BrowseOutputAsync()
    {
        var path = await Services.FolderPicker.PickFolderAsync("选择 SILK 导出目录").ConfigureAwait(true);
        if (path is not null)
        {
            OutputDirectory = path;
        }
    }

    public string ContactSelectionSummary
    {
        get
        {
            var contact = Services.Project.SelectedContact;
            return contact is null
                ? "尚未显式选择联系人。"
                : $"最终联系人：备注「{contact.Remark ?? "（无）"}」；昵称「{contact.Nickname ?? "（无）"}」；内部 username：{contact.Username}";
        }
    }

    public string SelectionPlanSummary
    {
        get
        {
            var plan = Services.Project.SelectionPlan;
            return plan is null
                ? "尚未完成不可变扫描计划。"
                : $"导出计划：仅对方发来的语音（incoming）；可导出 {plan.ScanReport.ExportableVoiceCount} 条；"
                    + $"时间 {FormatUtc(plan.FromUtc)} 至 {FormatUtc(plan.ToUtc)}；计划指纹 {ShortFingerprint(plan.PlanFingerprint)}";
        }
    }

    protected override void OnProjectPropertyChanged(string? propertyName)
    {
        if (propertyName == nameof(ExportProjectSession.WorkspacePath))
        {
            WorkspacePath = Services.Project.WorkspacePath;
        }

        if (propertyName is nameof(ExportProjectSession.Workspace)
            or nameof(ExportProjectSession.SelectedContact)
            or nameof(ExportProjectSession.Scan)
            or nameof(ExportProjectSession.SelectionPlan))
        {
            ApplyOutputDefaults();
        }

        if (propertyName is nameof(ExportProjectSession.SelectedContact)
            or nameof(ExportProjectSession.SelectionPlan)
            or nameof(ExportProjectSession.Scan)
            or nameof(ExportProjectSession.Workspace)
            or nameof(ExportProjectSession.LastExportRun)
            or nameof(ExportProjectSession.ExportDirectory))
        {
            OnPropertyChanged(nameof(ContactSelectionSummary));
            OnPropertyChanged(nameof(SelectionPlanSummary));
            OnPropertyChanged(nameof(CanExport));
            ApplyOutputDefaults();
        }

        if (propertyName is nameof(ExportProjectSession.WorkspacePath)
            or nameof(ExportProjectSession.ExportDirectory))
        {
            OnPropertyChanged(nameof(CanExport));
            OnPropertyChanged(nameof(OutputDirectorySummary));
        }
    }

    private static string FormatUtc(DateTimeOffset? value)
        => value?.ToUniversalTime().ToString("O") ?? "不限";

    private static string ShortFingerprint(string fingerprint)
        => fingerprint.Length <= 16 ? fingerprint : fingerprint[..16] + "…";

    private bool IsCurrentExportSelection(PreparedVoiceSelection plan, ContactRecord contact, string? workspacePath)
        => ReferenceEquals(Services.Project.SelectionPlan, plan)
            && ReferenceEquals(Services.Project.Scan?.Report, plan.ScanReport)
            && Services.Project.SelectedContact is { } current
            && string.Equals(current.ContactId, contact.ContactId, StringComparison.Ordinal)
            && string.Equals(current.Username, contact.Username, StringComparison.Ordinal)
            && (string.IsNullOrWhiteSpace(workspacePath)
                || string.IsNullOrWhiteSpace(Services.Project.WorkspacePath)
                || PathsEqual(workspacePath, Services.Project.WorkspacePath));

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
