using Avalonia.Controls;

namespace WeChatVoice.Desktop;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
        Closing += (_, _) => (DataContext as ViewModels.MainWindowViewModel)?.CancelActiveOperations();
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (DataContext is ViewModels.MainWindowViewModel viewModel)
        {
            await viewModel.ActivateSelectedPageAsync().ConfigureAwait(true);
        }
    }
}
