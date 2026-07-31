using WeChatVoice.Core.Models;

namespace WeChatVoice.Core.Ports;

/// <summary>
/// Observes whether a known producer process can still mutate the selected
/// source. Implementations must not open process memory or elevate privileges.
/// </summary>
public interface ISnapshotSourceActivityProbe
{
    SnapshotSourceActivity Probe();
}
