using WeChatVoice.KeyAcquisition.Ports;
using WeChatVoice.KeyAcquisition.Validation;
using WeChatVoice.Windows;

namespace WeChatVoice.KeyBroker;

/// <summary>
/// Reviewed candidate Profiles known to this build. These are discoverable
/// for diagnostics, but a Profile is not considered usable until a matching
/// plaintext materializer is registered by the Broker host.
/// </summary>
public static class GuardedKeyExtractionProfiles
{
    internal static IReadOnlyList<IWeixinKeyExtractionProfile> Create(Action<ProcessMemoryScanResult>? scanProgress = null) =>
    [
        new WeixinWindows41155Profile(
            new WeixinWindows4SqlCipherKeyValidator(),
            new WindowsWeixinProcessMemorySourceFactory(),
            scanProgress),
    ];
}
