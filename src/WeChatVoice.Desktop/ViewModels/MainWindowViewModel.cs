using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using WeChatVoice.Desktop.Infrastructure;

namespace WeChatVoice.Desktop.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly DesktopServices _services;
    private readonly SemaphoreSlim _navigationGate = new(1, 1);
    private readonly object _navigationSync = new();
    private CancellationTokenSource? _navigationCancellation;
    private PageViewModelBase? _activePage;

    public MainWindowViewModel(DesktopServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
        var marshal = services.InvokeOnUi
            ?? (static action => Dispatcher.UIThread.InvokeAsync(action).GetTask());
        IsDevelopmentBrokerEnabled = services.Workflows.AllowDevelopmentBroker;
        Pages =
        [
            new ResumeViewModel(services),
            new EnvironmentViewModel(services),
            new SourceSnapshotViewModel(services),
            new MaterializationViewModel(services),
            new ContactViewModel(services),
            new ScanViewModel(services),
            new ExportViewModel(services),
            new DatasetCurationViewModel(services),
            new HistoryDiagnosticsViewModel(services),
            new StorageViewModel(services),
        ];
        _selectedPage = Pages[0];
        services.Navigation.NavigationRequested += requestedType =>
        {
            var target = Pages.FirstOrDefault(page => page.GetType() == requestedType);
            if (target is not null)
            {
                _ = marshal(() => SelectedPage = target);
            }
        };
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
                NavigationTask = StartNavigation(value);
            }
        }
    }

    /// <summary>
    /// The current page activation task. The window observes this task through
    /// <see cref="ActivateSelectedPageAsync"/>; keeping it public also gives
    /// headless hosts a deterministic await point.
    /// </summary>
    public Task NavigationTask { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Activates the initially selected page. The constructor deliberately does
    /// not start asynchronous work, so the window owns the first activation.
    /// </summary>
    public Task ActivateSelectedPageAsync(CancellationToken cancellationToken = default)
    {
        NavigationTask = StartNavigation(SelectedPage, cancellationToken);
        return NavigationTask;
    }

    public void CancelNavigation()
    {
        lock (_navigationSync)
        {
            _navigationCancellation?.Cancel();
        }

        foreach (var page in Pages)
        {
            if (page.RunHost.CanCancel)
            {
                page.RunHost.CancelCommand.Execute(null);
            }
        }
    }

    private Task StartNavigation(PageViewModelBase target, CancellationToken externalCancellation = default)
    {
        CancellationTokenSource cancellation;
        lock (_navigationSync)
        {
            _navigationCancellation?.Cancel();
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(externalCancellation);
            _navigationCancellation = cancellation;
        }

        return NavigateAsync(target, cancellation);
    }

    private async Task NavigateAsync(PageViewModelBase target, CancellationTokenSource cancellation)
    {
        try
        {
            await _navigationGate.WaitAsync(cancellation.Token).ConfigureAwait(false);
            try
            {
                var previous = _activePage;
                if (previous is not null && !ReferenceEquals(previous, target))
                {
                    await previous.OnNavigatedFromAsync(cancellation.Token).ConfigureAwait(false);
                }

                cancellation.Token.ThrowIfCancellationRequested();
                await target.OnNavigatedToAsync(cancellation.Token).ConfigureAwait(false);

                lock (_navigationSync)
                {
                    if (ReferenceEquals(_navigationCancellation, cancellation))
                    {
                        _activePage = target;
                    }
                }
            }
            finally
            {
                _navigationGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Leaving a page is a normal lifecycle event, not a user-visible
            // workflow failure.
        }
        catch
        {
            // Page activation must not create an unobserved exception from a
            // binding setter. The page exposes its own typed operation state;
            // navigation only reports a non-sensitive retry hint.
            var invoke = _services.InvokeOnUi
                ?? (action => Dispatcher.UIThread.InvokeAsync(action).GetTask());
            await invoke(() => NavigationHint = "页面初始化失败，请重新检测后重试。").ConfigureAwait(false);
        }
        finally
        {
            lock (_navigationSync)
            {
                if (ReferenceEquals(_navigationCancellation, cancellation))
                {
                    _navigationCancellation = null;
                }
            }

            cancellation.Dispose();
        }
    }

    [ObservableProperty]
    private string? _navigationHint;

    public void CancelActiveOperations()
    {
        CancelNavigation();
    }
}
