using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace WeChatVoice.Desktop.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    public MainWindowViewModel(DesktopServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        IsDevelopmentBrokerEnabled = services.Workflows.AllowDevelopmentBroker;
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

    public bool IsDevelopmentBrokerEnabled { get; }

    public string DevelopmentTrustBanner =>
        "开发信任模式已启用：未签名 Broker 只允许用于开发数据，禁止用于正式发布。";

    public ObservableCollection<PageViewModelBase> Pages { get; }

    private PageViewModelBase _selectedPage = null!;

    public PageViewModelBase SelectedPage
    {
        get => _selectedPage;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!value.CanNavigate)
            {
                NavigationHint = value.NavigationHint;
                return;
            }

            if (SetProperty(ref _selectedPage, value))
            {
                NavigationHint = null;
            }
        }
    }

    [ObservableProperty]
    private string? _navigationHint;
}
