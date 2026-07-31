using WeChatVoice.KeyAcquisition.Ports;
using WeChatVoice.KeyAcquisition.Validation;

namespace WeChatVoice.KeyBroker;

/// <summary>
/// Reviewed Profiles known to this build. The Broker still requires a matching
/// fixed plaintext materializer before one can be selected.
/// </summary>
public static class GuardedKeyExtractionProfiles
{
    internal static IReadOnlyList<IWeixinKeyExtractionProfile> Create(Action<WeixinKeyScanProgress>? scanProgress = null) =>
    [
        new WeixinWindows41155Profile(
            new WeixinWindows41155SqlCipherKeyValidator(),
            new WindowsWeixinProcessMemorySourceFactory(),
            new VersionedWcdbModuleIdentityVerifier(),
            scanProgress),
    ];
}
