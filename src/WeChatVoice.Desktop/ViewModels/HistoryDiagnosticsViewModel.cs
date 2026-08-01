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

    [RelayCommand]
    private void Refresh()
    {
        RecentWorkspaces = Services.RecentWorkspaces.Load();
        LogLines = Services.Log.RecentLines.ToArray();
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
}
