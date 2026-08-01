using WeChatVoice.Core.Errors;
using WeChatVoice.KeyAcquisition.Ports;
using WeChatVoice.Windows;

namespace WeChatVoice.KeyBroker;

internal sealed record WeixinProcessIdentityPolicy(
    string ProductVersion,
    string ImageSha256,
    string OwnerSid,
    int SessionId,
    string Architecture,
    string SignerSubjectFragment = "Tencent");

internal sealed class ProcessIdentityVerifier(IWeixinProcessIdentityReader reader)
{
    private readonly IWeixinProcessIdentityReader reader = reader ?? throw new ArgumentNullException(nameof(reader));

    internal VerifiedWeixinProcess Verify(int processId, WeixinProcessIdentityPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var before = reader.Read(processId);
        Validate(before, policy);
        var after = reader.Read(processId);
        Validate(after, policy);

        if (before.ProcessId != after.ProcessId ||
            before.StartedAtUtc != after.StartedAtUtc ||
            before.SessionId != after.SessionId ||
            !string.Equals(before.ImagePath, after.ImagePath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(before.ImageSha256, after.ImageSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new AppFailureException(ErrorCode.ProcessIdentityMismatch, "The Weixin process identity changed while it was being verified.");
        }

        return new VerifiedWeixinProcess(
            after.ProcessId,
            after.StartedAtUtc,
            after.ImagePath,
            after.ImageSha256.ToLowerInvariant(),
            after.ProductVersion,
            after.OwnerSid,
            after.SessionId,
            after.Architecture);
    }

    private static void Validate(WeixinProcessIdentityEvidence evidence, WeixinProcessIdentityPolicy policy)
    {
        if (evidence.ProcessId <= 0 || !string.Equals(evidence.ProcessName, "Weixin", StringComparison.OrdinalIgnoreCase))
        {
            throw new AppFailureException(ErrorCode.ProcessIdentityMismatch, "The target is not the fixed Weixin process.");
        }

        if (!string.Equals(evidence.ProductVersion, policy.ProductVersion, StringComparison.Ordinal))
        {
            throw new AppFailureException(ErrorCode.UnsupportedWeixinVersion, "The verified Weixin version does not match the selected Profile.");
        }

        if (!string.Equals(evidence.ImageSha256, policy.ImageSha256, StringComparison.OrdinalIgnoreCase) ||
            !evidence.HasTrustedSignature ||
            !evidence.SignerSubject.Contains(policy.SignerSubjectFragment, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(evidence.OwnerSid, policy.OwnerSid, StringComparison.Ordinal) ||
            evidence.SessionId != policy.SessionId ||
            !string.Equals(evidence.Architecture, policy.Architecture, StringComparison.OrdinalIgnoreCase))
        {
            throw new AppFailureException(ErrorCode.ProcessIdentityMismatch, "The Weixin process did not satisfy the selected profile identity policy.");
        }
    }
}
