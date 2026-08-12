using WeChatVoice.Core.Models;
using WeChatVoice.Infrastructure.SeedVc;

namespace WeChatVoice.Tests;

public sealed class SeedVcToolchainResolverTests
{
    [Fact]
    public void Resolve_prefers_explicit_then_environment_then_global_file()
    {
        using var temp = new TestTemporaryDirectory();
        var configPath = Path.Combine(temp.RootPath, "config", "toolchain.json");
        var resolver = new SeedVcToolchainResolver(configPath);
        resolver.Save(new SeedVcToolchainConfiguration("global-root", "global-python", "global-ffmpeg", LinuxHost: "chatapp-linux"));
        var previous = Environment.GetEnvironmentVariable("WECHATVOICE_PYTHON");
        try
        {
            Environment.SetEnvironmentVariable("WECHATVOICE_PYTHON", "environment-python");
            var fromEnvironment = resolver.Resolve();
            Assert.Equal("environment-python", fromEnvironment.PythonPath);
            Assert.Equal("chatapp-linux", fromEnvironment.LinuxHost);
            Assert.Equal("explicit-python", resolver.Resolve(pythonOverride: "explicit-python").PythonPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("WECHATVOICE_PYTHON", previous);
        }
    }

    [Fact]
    public void Save_is_atomic_and_round_trips_linux_ssh_settings()
    {
        using var temp = new TestTemporaryDirectory();
        var path = Path.Combine(temp.RootPath, "nested", "toolchain.json");
        var resolver = new SeedVcToolchainResolver(path);
        resolver.Save(new SeedVcToolchainConfiguration(
            SeedVcRoot: "/opt/seed-vc",
            PythonPath: "/opt/venv/bin/python",
            FfmpegPath: "/usr/bin/ffmpeg",
            LinuxHost: "chatapp-linux",
            LinuxUser: "yeluo",
            LinuxPort: 22,
            LinuxSeedVcRoot: "/home/yeluo/seed-vc",
            LinuxPythonPath: "/home/yeluo/.venv/bin/python",
            LinuxFfmpegPath: "/usr/bin/ffmpeg"));

        var loaded = resolver.Load();
        Assert.Equal(SeedVcToolchainConfiguration.CurrentFormat, loaded.Format);
        Assert.Equal("chatapp-linux", loaded.LinuxHost);
        Assert.Equal(22, loaded.LinuxPort);
        Assert.True(File.Exists(path));
        Assert.DoesNotContain("key", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Linux_default_config_uses_xdg_when_available()
    {
        using var temp = new TestTemporaryDirectory();
        var previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", temp.RootPath);
            var resolver = new SeedVcToolchainResolver();
            if (OperatingSystem.IsLinux())
            {
                Assert.Equal(Path.Combine(temp.RootPath, "wechatvoice", "toolchain.json"), resolver.GlobalConfigPath);
            }
            else
            {
                Assert.NotEmpty(resolver.GlobalConfigPath);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
        }
    }

    [Fact]
    public void Linux_configuration_falls_back_to_linux_toolchain_fields_on_linux()
    {
        using var temp = new TestTemporaryDirectory();
        var resolver = new SeedVcToolchainResolver(Path.Combine(temp.RootPath, "toolchain.json"));
        resolver.Save(new SeedVcToolchainConfiguration(
            LinuxSeedVcRoot: "/remote/seed-vc",
            LinuxPythonPath: "/remote/python",
            LinuxFfmpegPath: "/usr/bin/ffmpeg"));
        var resolved = resolver.Resolve();
        if (OperatingSystem.IsLinux())
        {
            Assert.Equal("/remote/seed-vc", resolved.SeedVcRoot);
            Assert.Equal("/remote/python", resolved.PythonPath);
            Assert.Equal("/usr/bin/ffmpeg", resolved.FfmpegPath);
        }
        else
        {
            Assert.Null(resolved.SeedVcRoot);
        }
    }
}
