using Avalonia.Controls;

namespace WeChatVoice.Desktop;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closing += (_, _) => (DataContext as ViewModels.MainWindowViewModel)?.CancelActiveOperations();
    }
}
