using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WeChatVoice.Core.Errors;
using WeChatVoice.Desktop.Infrastructure;
using WeChatVoice.Workflows.Workflows;

namespace WeChatVoice.Desktop.ViewModels;

/// <summary>
/// History and diagnostics page: recent verified workspaces (LocalApplicationData
/// only) and the scrubbed session log. Nothing here contains contact data,
/// keys, memory contents, or database content.
/// </summary>
public sealed partial class HistoryDiagnosticsViewModel : PageViewModelBase
{
    public HistoryDiagnosticsViewModel(DesktopServices services)
        : base(services)
    {
        // Metadata is safe to load at startup; the JSON is still untrusted and
        // is only opened after the user explicitly invokes Load and Verify.
        Refresh();
    }

    public override string Title => "历史与诊断";

    [ObservableProperty]
    private IReadOnlyList<RecentWorkspaceEntry> _recentWorkspaces = [];

    [ObservableProperty]
    private RecentWorkspaceEntry? _selectedWorkspace;

    partial void OnSelectedWorkspaceChanged(RecentWorkspaceEntry? value)
    {
        _deleteArmed = false;
        _deletePreview = null;
        SelectedExportDirectory = value?.LastExportDirectory;
        ExportRuns = LoadExportRuns(SelectedExportDirectory);
    }

    [ObservableProperty]
    private IReadOnlyList<string> _logLines = [];

    [ObservableProperty]
    private string _diagnosticsSummary = "本页只显示阶段、错误码与时长；不记录联系人、密钥、内存内容或数据库数据。";

    [ObservableProperty]
    private string? _workspaceDeleteSummary;
    [ObservableProperty]
    private string? _materializedRootPath;
    [ObservableProperty]
    private string? _materializationManifestSummary;
    [ObservableProperty]
    private string? _selectedExportDirectory;
    [ObservableProperty] private IReadOnlyList<ExportRunHistoryEntry> _exportRuns = [];
    private bool _deleteArmed;
    private WorkspaceDeletionPreview? _deletePreview;

    [RelayCommand]
    private Task LoadSelectedWorkspaceAsync()
    {
        var selectedPath = SelectedWorkspace?.WorkspacePath;
        return RunHost.RunAsync(
        async (context, cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(selectedPath)) throw new AppFailureException(ErrorCode.InvalidRequest, "Please select a recent Workspace.");
            return await Workflows.Workspace.VerifyAsync(selectedPath, context, cancellationToken).ConfigureAwait(false);
        },
        result =>
        {
            Services.Project.ClearVoiceSelection(clearContact: true);
            Services.Project.Workspace = result;
            Services.Project.WorkspacePath = selectedPath;
            Services.Project.ExportDirectory = SelectedWorkspace?.LastExportDirectory;
            MaterializedRootPath = result.Workspace.SourceRoot;
            MaterializationManifestSummary = result.Workspace.Provenance is null
                ? "此 Workspace 没有物料化 provenance；已完成普通 Workspace 验证。"
                : "物料化 Manifest 已随 Workspace provenance 验证通过。";
            SelectedExportDirectory = SelectedWorkspace?.LastExportDirectory;
            ExportRuns = LoadExportRuns(SelectedExportDirectory);
            WorkspaceDeleteSummary = $"Workspace 已验证并恢复：{result.Workspace.WorkspaceId}";
        });
    }

    [RelayCommand]
    private Task DeleteMaterializedWorkspaceAsync()
    {
        if (SelectedWorkspace is null)
        {
            WorkspaceDeleteSummary = "请先选择 Workspace。";
            return Task.CompletedTask;
        }
        if (!_deleteArmed)
        {
            var previewPath = SelectedWorkspace.WorkspacePath;
            return RunHost.RunAsync(
                async (context, cancellationToken) => await Workflows.Workspace.PreviewDeleteMaterializedAsync(
                    previewPath,
                    context,
                    cancellationToken).ConfigureAwait(false),
                preview =>
                {
                    if (!string.Equals(SelectedWorkspace?.WorkspacePath, previewPath, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new AppFailureException(ErrorCode.InvalidRequest, "Workspace selection changed; run the deletion preview again.");
                    }

                    _deletePreview = preview;
                    _deleteArmed = true;
                    WorkspaceDeleteSummary = $"已验证将删除 {preview.DatabaseCount} 个明文数据库，共 {preview.TotalBytes} bytes。再次点击确认；源快照和 SILK 导出不会删除。";
                });
        }
        if (_deletePreview is null)
        {
            _deleteArmed = false;
            WorkspaceDeleteSummary = "删除预检已失效，请重新执行预检。";
            return Task.CompletedTask;
        }
        _deleteArmed = false;
        _deletePreview = null;
        var path = SelectedWorkspace.WorkspacePath;
        return RunHost.RunAsync(
            async (context, cancellationToken) => await Workflows.Workspace.DeleteMaterializedAsync(path, context, cancellationToken).ConfigureAwait(false),
            result =>
            {
                WorkspaceDeleteSummary = $"已删除明文 Workspace：{result.DatabaseCount} 个数据库，{result.TotalBytes} bytes。";
                Services.RecentWorkspaces.Remove(path);
                if (string.Equals(Services.Project.WorkspacePath, path, StringComparison.OrdinalIgnoreCase))
                {
                    Services.Project.ClearVoiceSelection(clearContact: true);
                    Services.Project.Workspace = null;
                    Services.Project.WorkspacePath = null;
                }
                MaterializedRootPath = null;
                MaterializationManifestSummary = null;
                SelectedExportDirectory = null;
                Refresh();
            });
    }

    [RelayCommand]
    private Task RepairSelectedWorkspaceAsync()
    {
        var outputRoot = MaterializedRootPath;
        var workspacePath = SelectedWorkspace?.WorkspacePath;
        if (string.IsNullOrWhiteSpace(outputRoot) || string.IsNullOrWhiteSpace(workspacePath))
        {
            WorkspaceDeleteSummary = "请先加载并验证需要修复的 Workspace，并确认明文数据库目录可用。";
            return Task.CompletedTask;
        }

        return RunHost.RunAsync(
            async (context, cancellationToken) => await Workflows.Workspace.RepairMaterializationAsync(
                new MaterializationRecoveryRequest(outputRoot, workspacePath),
                context,
                cancellationToken).ConfigureAwait(false),
            result =>
            {
                Services.Project.Workspace = result;
                Services.Project.WorkspacePath = workspacePath;
                Services.RecentWorkspaces.Add(result, workspacePath);
                WorkspaceDeleteSummary = "Workspace JSON 已根据已验证的 Completed 物料化重新生成。";
                MaterializedRootPath = result.Workspace.SourceRoot;
                MaterializationManifestSummary = "物料化 Manifest、输出 Hash 与账号身份已重新验证。";
                Refresh();
            });
    }

    [RelayCommand]
    private void Refresh()
    {
        RecentWorkspaces = Services.RecentWorkspaces.Load();
        LogLines = Services.Log.GetRecentSnapshot();
        SelectedExportDirectory = SelectedWorkspace?.LastExportDirectory;
        ExportRuns = LoadExportRuns(SelectedExportDirectory);
    }

    private IReadOnlyList<ExportRunHistoryEntry> LoadExportRuns(string? selectedExportDirectory = null)
    {
        var root = selectedExportDirectory;
        if (string.IsNullOrWhiteSpace(root)
            && SelectedWorkspace is not null
            && string.Equals(SelectedWorkspace.WorkspacePath, Services.Project.WorkspacePath, StringComparison.OrdinalIgnoreCase))
        {
            root = Services.Project.ExportDirectory;
        }
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(Path.Combine(root, "runs"))) return [];
        var results = new List<ExportRunHistoryEntry>();
        foreach (var path in Directory.EnumerateFiles(Path.Combine(root, "runs"), "*.manifest.json"))
        {
            try
            {
                using var stream = File.OpenRead(path);
                var manifest = System.Text.Json.JsonSerializer.Deserialize<Core.Models.VoiceExportManifest>(stream);
                if (manifest is not null) results.Add(new ExportRunHistoryEntry(manifest.RunId, manifest.RunStatus, manifest.Entries.Count, manifest.Failures.Count, File.GetLastWriteTimeUtc(path)));
            }
            catch (IOException) { }
            catch (System.Text.Json.JsonException) { }
        }
        return results.OrderByDescending(item => item.GeneratedAtUtc).ToArray();
    }

    public sealed record ExportRunHistoryEntry(string RunId, Core.Models.ExportRunStatus Status, int EntryCount, int FailureCount, DateTime GeneratedAtUtc);

    [RelayCommand]
    private void RemoveSelectedWorkspace()
    {
        if (SelectedWorkspace is not null)
        {
            Services.RecentWorkspaces.Remove(SelectedWorkspace.WorkspacePath);
            Refresh();
        }
    }

    [RelayCommand]
    private void DeleteSelectedWorkspaceFile()
    {
        if (SelectedWorkspace is null)
        {
            WorkspaceDeleteSummary = "请先选择 Workspace JSON。";
            return;
        }

        var path = Path.GetFullPath(SelectedWorkspace.WorkspacePath);
        if (!File.Exists(path) || Directory.Exists(path))
        {
            WorkspaceDeleteSummary = "Workspace JSON 不存在，未删除目录或其他数据。";
            Services.RecentWorkspaces.Remove(path);
            Refresh();
            return;
        }

        try
        {
            File.Delete(path);
            Services.RecentWorkspaces.Remove(path);
            if (string.Equals(Services.Project.WorkspacePath, path, StringComparison.OrdinalIgnoreCase))
            {
                Services.Project.Workspace = null;
                Services.Project.WorkspacePath = null;
                Services.Project.ClearVoiceSelection(clearContact: true);
                MaterializedRootPath = null;
                MaterializationManifestSummary = null;
                SelectedExportDirectory = null;
            }

            WorkspaceDeleteSummary = "Workspace JSON 已删除；源快照和导出文件未删除。";
            Refresh();
        }
        catch (UnauthorizedAccessException)
        {
            WorkspaceDeleteSummary = "Workspace JSON 删除失败：权限不足。";
        }
        catch (IOException)
        {
            WorkspaceDeleteSummary = "Workspace JSON 删除失败：文件正在使用或不可用。";
        }
    }
}
