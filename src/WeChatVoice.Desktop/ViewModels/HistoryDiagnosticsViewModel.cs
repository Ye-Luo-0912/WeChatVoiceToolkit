using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    [RelayCommand]
    private void Refresh()
    {
        RecentWorkspaces = Services.RecentWorkspaces.Load();
        LogLines = Services.Log.GetRecentSnapshot();
    }

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
