using System.Diagnostics;

namespace WeChatVoice.Desktop.Tests;

/// <summary>
/// Publish/launch smoke: the built Desktop apphost must start, run the headless
/// smoke suite (composition root, Workflow State Machine, recent-workspaces
/// store, scrubbed log), and exit 0. No window is created, so it is safe in CI.
/// </summary>
public sealed class DesktopSmokeTests
{
    [Fact]
    public async Task Desktop_exe_passes_smoke_check()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "WeChatVoice.Desktop.exe");
        Assert.True(File.Exists(exe), "The Desktop apphost was not copied to the test output.");

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            Arguments = "--smoke-check",
            UseShellExecute = false,
        });
        Assert.NotNull(process);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await process.WaitForExitAsync(timeout.Token);
        Assert.Equal(0, process.ExitCode);
    }
}
