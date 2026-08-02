using Avalonia;
using Avalonia.Headless;
using WeChatVoice.Desktop.Infrastructure;
using WeChatVoice.Desktop.ViewModels;
using WeChatVoice.Desktop.Views;

namespace WeChatVoice.Desktop.Tests;

public sealed class AvaloniaHeadlessSmokeTests
{
    [Fact]
    public void Main_window_and_all_page_views_load_under_headless_platform()
    {
        var app = AppBuilder.Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions())
            .SetupWithoutStarting();
        var services = DesktopServices.Create(appDataDirectory: Path.Combine(Path.GetTempPath(), "WeChatVoiceToolkit.Headless", Guid.NewGuid().ToString("N")));
        var window = new MainWindow { DataContext = new MainWindowViewModel(services) };

        Assert.NotNull(window);
        Assert.NotNull(new EnvironmentView());
        Assert.NotNull(new SourceSnapshotView());
        Assert.NotNull(new MaterializationView());
        Assert.NotNull(new ContactView());
        Assert.NotNull(new ScanView());
        Assert.NotNull(new ExportView());
        Assert.NotNull(new HistoryDiagnosticsView());
    }

    [Fact]
    public async Task Folder_picker_requires_a_real_attached_storage_owner()
    {
        var picker = new DesktopFolderPicker();
        await Assert.ThrowsAsync<InvalidOperationException>(() => picker.PickFolderAsync("test"));
    }
}
