using WeChatVoice.KeyAcquisition.Ports;
using WeChatVoice.KeyAcquisition.Validation;

namespace WeChatVoice.KeyBroker;

/// <summary>
/// Reviewed candidate Profiles known to this build. These are discoverable
/// for diagnostics, but a Profile is not considered usable until a matching
/// plaintext materializer is registered by the Broker host.
/// </summary>
public static class GuardedKeyExtractionProfiles
{
    public static IReadOnlyList<IWeixinKeyExtractionProfile> Create() =>
    [
        new WeixinWindows41155Profile(
            new WeixinWindows4SqlCipherKeyValidator(),
            new WindowsWeixinProcessMemorySourceFactory()),
    ];
}
