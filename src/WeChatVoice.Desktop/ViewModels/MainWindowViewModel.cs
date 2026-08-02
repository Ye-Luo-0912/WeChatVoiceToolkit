using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using WeChatVoice.Desktop.Infrastructure;

namespace WeChatVoice.Desktop.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly DesktopServices _services;

    public MainWindowViewModel(DesktopServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
        var marshal = services.InvokeOnUi
            ?? (static action => Dispatcher.UIThread.InvokeAsync(action).GetTask());
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
        services.OperationCoordinator.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName is nameof(OperationCoordinator.IsBusy) or null)
            {
                _ = marshal(() => OnPropertyChanged(nameof(IsOperationBusy)));
            }
        };
    }

    public bool IsDevelopmentBrokerEnabled { get; }

    public string DevelopmentTrustBanner =>
        "开发信任模式已启用：未签名 Broker 只允许用于开发数据，禁止用于正式发布。";

    public bool IsOperationBusy => _services.OperationCoordinator.IsBusy;

    public ObservableCollection<PageViewModelBase> Pages { get; }

    private PageViewModelBase _selectedPage = null!;

    public PageViewModelBase SelectedPage
    {
        get => _selectedPage;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (IsOperationBusy && !ReferenceEquals(value, _selectedPage))
            {
                NavigationHint = "当前操作进行中；账号确认或 UAC 完成前不能切换页面。";
                return;
            }

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

    public void CancelActiveOperations()
    {
        foreach (var page in Pages)
        {
            if (page.RunHost.CanCancel) page.RunHost.CancelCommand.Execute(null);
        }
    }
}
