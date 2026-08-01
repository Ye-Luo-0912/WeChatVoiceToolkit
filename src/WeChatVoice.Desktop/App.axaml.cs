using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using WeChatVoice.Desktop.ViewModels;

namespace WeChatVoice.Desktop;

public sealed partial class App : Avalonia.Application
{
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
            var mainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(services),
            };
            services.FolderPicker.Attach(mainWindow);
            desktop.MainWindow = mainWindow;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
