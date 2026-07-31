using WeChatVoice.Windows;

namespace WeChatVoice.Tests;

public sealed class WindowsSafetyTests
{
    [Fact]
    public void SensitiveBuffer_copies_exactly_sized_data_and_clear_zeros_the_publicly_observable_contents()
    {
        using var buffer = new SensitiveBuffer(new byte[] { 5, 10, 15 });
        var observed = new byte[3];

        buffer.CopyTo(observed);
        Assert.Equal(new byte[] { 5, 10, 15 }, observed);

        buffer.CopyFrom(new byte[] { 20, 25, 30 });
        buffer.CopyTo(observed);
        Assert.Equal(new byte[] { 20, 25, 30 }, observed);

        buffer.Clear();
        buffer.CopyTo(observed);
        Assert.Equal(new byte[] { 0, 0, 0 }, observed);

        Assert.Throws<ArgumentException>(() => buffer.CopyFrom(new byte[] { 1, 2 }));
        Assert.Throws<ArgumentException>(() => buffer.CopyTo(new byte[2]));
    }

    [Fact]
    public void SensitiveBuffer_rejects_negative_lengths_and_is_not_usable_after_disposal()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SensitiveBuffer(-1));

        var buffer = new SensitiveBuffer(1);
        buffer.Dispose();
        buffer.Dispose();

        Assert.Throws<ObjectDisposedException>(() => _ = buffer.Length);
        Assert.Throws<ObjectDisposedException>(() => buffer.Clear());
        Assert.Throws<ObjectDisposedException>(() => buffer.CopyTo(new byte[1]));
    }

    [Fact]
    public void WeChat_process_discovery_is_limited_to_its_fixed_supported_names()
    {
        var supportedNames = WeChatProcessDiscovery.SupportedProcessNames;

        Assert.Equal(new[] { "WeChat", "WeChatAppEx", "Weixin" }, supportedNames);
        var mutableView = Assert.IsAssignableFrom<IList<string>>(supportedNames);
        Assert.Throws<NotSupportedException>(() => mutableView.Add("arbitrary-process"));

        var running = WeChatProcessDiscovery.ListRunning();
        Assert.Equal(running.OrderBy(process => process.ProcessId), running);
        Assert.All(running, process =>
        {
            Assert.True(process.ProcessId > 0);
            Assert.Contains(supportedNames, name => string.Equals(name, process.ProcessName, StringComparison.OrdinalIgnoreCase));
        });
    }

    [Fact]
    public void Broker_only_memory_session_has_fixed_non_expandable_limits()
    {
        Assert.Equal(1024 * 1024, WeixinProcessMemorySession.ChunkSize);
        Assert.Equal(128L * 1024 * 1024, WeixinProcessMemorySession.MaximumRegionBytes);
        Assert.Equal(768L * 1024 * 1024, WeixinProcessMemorySession.MaximumTotalBytes);
        Assert.Equal(8192, WeixinProcessMemorySession.MaximumRegions);
        Assert.Equal(TimeSpan.FromSeconds(30), WeixinProcessMemorySession.MaximumDuration);
    }
}
