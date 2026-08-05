namespace WeChatVoice.Workflows.Broker;

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

public sealed record WorkerBundleTrustResult(bool Verified, string? NonSensitiveReason)
{
    public static WorkerBundleTrustResult Ok() => new(true, null);
    public static WorkerBundleTrustResult Deny(string reason) => new(false, reason);
}

/// <summary>
/// Security assessment state for the directory that contains the elevated
/// Broker. A trust-chain failure before the ACL probe is not evidence that the
/// directory is indeterminate; it is explicitly NotEvaluated.
/// </summary>
public enum InstallSecurityState
{
    VerifiedProtected,
    UserWritable,
    Indeterminate,
    NotEvaluated,
    DevelopmentModeNotApplicable,
}

public sealed record InstallDirectorySecurityResult(
    bool Protected,
    bool UserWritable,
    string? NonSensitiveReason,
    UserWriteability Writeability = UserWriteability.Indeterminate,
    InstallSecurityState SecurityState = InstallSecurityState.NotEvaluated);
