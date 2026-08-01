using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace WeChatVoice.Desktop.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    public MainWindowViewModel(DesktopServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        Pages =
        [
            new EnvironmentViewModel(services),
            new SourceSnapshotViewModel(services),
            new MaterializationViewModel(services),
            new ContactViewModel(services),
            new ScanViewModel(services),
            new ExportViewModel(services),
            new HistoryDiagnosticsViewModel(services),
        ];
        SelectedPage = Pages[0];
    }

    public ObservableCollection<PageViewModelBase> Pages { get; }

    [ObservableProperty]
    private PageViewModelBase _selectedPage;
}
