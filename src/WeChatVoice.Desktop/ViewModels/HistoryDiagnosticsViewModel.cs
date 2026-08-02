using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WeChatVoice.Core.Errors;
using WeChatVoice.Desktop.Infrastructure;

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
    }

    public override string Title => "历史与诊断";

    [ObservableProperty]
    private IReadOnlyList<RecentWorkspaceEntry> _recentWorkspaces = [];

    [ObservableProperty]
    private RecentWorkspaceEntry? _selectedWorkspace;

    [ObservableProperty]
    private IReadOnlyList<string> _logLines = [];

    [ObservableProperty]
    private string _diagnosticsSummary = "本页只显示阶段、错误码与时长；不记录联系人、密钥、内存内容或数据库数据。";

    [ObservableProperty]
    private string? _workspaceDeleteSummary;
    [ObservableProperty] private IReadOnlyList<ExportRunHistoryEntry> _exportRuns = [];
    private bool _deleteArmed;

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
            Services.Project.Workspace = result;
            Services.Project.WorkspacePath = selectedPath;
            Services.Project.Scan = null;
            Services.Project.SelectionPlan = null;
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
            _deleteArmed = true;
            WorkspaceDeleteSummary = "将重新验证并删除 Manifest 覆盖的明文 Workspace。再次点击确认；源快照和 SILK 导出不会删除。";
            return Task.CompletedTask;
        }
        _deleteArmed = false;
        var path = SelectedWorkspace.WorkspacePath;
        return RunHost.RunAsync(
            async (context, cancellationToken) => await Workflows.Workspace.DeleteMaterializedAsync(path, context, cancellationToken).ConfigureAwait(false),
            result =>
            {
                WorkspaceDeleteSummary = $"已删除明文 Workspace：{result.DatabaseCount} 个数据库，{result.TotalBytes} bytes。";
                Services.RecentWorkspaces.Remove(path);
                if (string.Equals(Services.Project.WorkspacePath, path, StringComparison.OrdinalIgnoreCase))
                {
                    Services.Project.Workspace = null;
                    Services.Project.WorkspacePath = null;
                }
                Refresh();
            });
    }

    [RelayCommand]
    private void Refresh()
    {
        RecentWorkspaces = Services.RecentWorkspaces.Load();
        LogLines = Services.Log.GetRecentSnapshot();
        ExportRuns = LoadExportRuns();
    }

    private IReadOnlyList<ExportRunHistoryEntry> LoadExportRuns()
    {
        var root = Services.Project.ExportDirectory;
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
                Services.Project.SelectedContact = null;
                Services.Project.Scan = null;
                Services.Project.LastExportRun = null;
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
