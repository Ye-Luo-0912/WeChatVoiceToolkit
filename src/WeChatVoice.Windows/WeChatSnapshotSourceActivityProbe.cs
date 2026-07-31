using WeChatVoice.Core.Models;
using WeChatVoice.Core.Ports;

namespace WeChatVoice.Windows;

/// <summary>
/// Read-only process activity probe used by the snapshot command. It reports
/// metadata only and never opens a process handle or reads process memory.
/// </summary>
public sealed class WeChatSnapshotSourceActivityProbe : ISnapshotSourceActivityProbe
{
    public SnapshotSourceActivity Probe()
    {
        var processes = WeChatProcessDiscovery.ListRunning();
        return new SnapshotSourceActivity(
            processes.Count > 0,
            processes.Select(static process => process.ProcessName));
    }
}
