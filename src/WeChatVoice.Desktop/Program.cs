using Avalonia;

namespace WeChatVoice.Desktop;

public static class Program
{
    /// <summary>
    /// The Desktop host always runs at normal privilege (see app.manifest).
    /// --smoke-check runs the headless smoke suite without creating a window,
    /// so CI can verify the published app without an interactive session.
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--smoke-check", StringComparer.Ordinal))
        {
            return Smoke.SmokeCheckRunner.Run();
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
