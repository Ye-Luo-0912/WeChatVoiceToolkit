namespace WeChatVoice.Cli.Services.BrokerTrust;

/// <summary>Result of a Broker binary trust evaluation.</summary>
public sealed record BrokerTrustResult(bool Verified, string? NonSensitiveReason)
{
    public static BrokerTrustResult Ok() => new(true, null);

    public static BrokerTrustResult Deny(string reason) => new(false, reason);
}

/// <summary>
/// Decides whether the adjacent Key Broker executable may be elevated.
/// Development builds opt into a repository-build-directory policy; released
/// installs must pass full Authenticode, publisher-pinning, bundle-manifest,
/// and install-directory checks.
/// </summary>
public interface IBrokerTrustPolicy
{
    BrokerTrustResult Verify(string brokerPath);
}
