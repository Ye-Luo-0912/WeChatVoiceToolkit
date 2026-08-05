using WeChatVoice.Windows;

namespace WeChatVoice.Desktop.Infrastructure;

/// <summary>
/// Read-only Desktop port for the existing fixed-name Weixin process
/// discovery. It exposes metadata only and is injectable for Headless tests.
/// </summary>
public interface IWeixinProcessProbe
{
    IReadOnlyList<WeChatProcessInfo> ListRunning();
}

public sealed class WeixinProcessProbe : IWeixinProcessProbe
{
    public IReadOnlyList<WeChatProcessInfo> ListRunning()
        => WeChatProcessDiscovery.ListRunning();
}
