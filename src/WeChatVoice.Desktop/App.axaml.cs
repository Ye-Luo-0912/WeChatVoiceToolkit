using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using WeChatVoice.Desktop.ViewModels;

namespace WeChatVoice.Desktop;

public sealed partial class App : Avalonia.Application
{
    private DesktopServices? _services;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var allowDevelopmentBroker = false;
#if DEBUG
            allowDevelopmentBroker = desktop.Args.Contains("--allow-development-broker", StringComparer.Ordinal);
#endif
            var services = DesktopServices.Create(allowDevelopmentBroker);
            _services = services;
            var mainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(services),
            };
            services.FolderPicker.Attach(mainWindow);
            desktop.MainWindow = mainWindow;
            desktop.Exit += OnDesktopExit;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void OnDesktopExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        var services = Interlocked.Exchange(ref _services, null);
        services?.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
