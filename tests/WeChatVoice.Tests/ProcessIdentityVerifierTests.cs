using WeChatVoice.Core.Errors;
using WeChatVoice.KeyBroker;
using WeChatVoice.Windows;

namespace WeChatVoice.Tests;

public sealed class ProcessIdentityVerifierTests
{
    private static readonly WeixinProcessIdentityPolicy Policy = new(
        "4.1.11.55", new string('a', 64), "S-1-5-21-test", 7, "x64");

    [Fact]
    public void Verify_accepts_only_identical_before_and_after_evidence()
    {
        var evidence = ValidEvidence();
        var result = new ProcessIdentityVerifier(new FakeReader(evidence, evidence)).Verify(42, Policy);

        Assert.Equal(42, result.ProcessId);
        Assert.Equal("4.1.11.55", result.ProductVersion);
    }

    [Theory]
    [InlineData("Wrong", true, "Tencent", "S-1-5-21-test", 7, "x64")]
    [InlineData("Weixin", false, "Tencent", "S-1-5-21-test", 7, "x64")]
    [InlineData("Weixin", true, "Unknown", "S-1-5-21-test", 7, "x64")]
    [InlineData("Weixin", true, "Tencent", "S-1-5-21-other", 7, "x64")]
    [InlineData("Weixin", true, "Tencent", "S-1-5-21-test", 8, "x64")]
    [InlineData("Weixin", true, "Tencent", "S-1-5-21-test", 7, "x86")]
    public void Verify_rejects_wrong_name_signature_signer_sid_session_or_architecture(
        string name, bool signature, string signer, string sid, int session, string architecture)
    {
        var evidence = ValidEvidence() with
        {
            ProcessName = name,
            HasTrustedSignature = signature,
            SignerSubject = signer,
            OwnerSid = sid,
            SessionId = session,
            Architecture = architecture,
        };

        Assert.Throws<AppFailureException>(() => new ProcessIdentityVerifier(new FakeReader(evidence)).Verify(42, Policy));
    }

    [Fact]
    public void Verify_rejects_version_hash_and_pid_reuse_changes()
    {
        var versionFailure = Assert.Throws<AppFailureException>(() => new ProcessIdentityVerifier(new FakeReader(ValidEvidence() with { ProductVersion = "4.1.12.1" })).Verify(42, Policy));
        Assert.Equal(ErrorCode.UnsupportedWeixinVersion, versionFailure.Code);
        Assert.Throws<AppFailureException>(() => new ProcessIdentityVerifier(new FakeReader(ValidEvidence() with { ImageSha256 = new string('b', 64) })).Verify(42, Policy));
        Assert.Throws<AppFailureException>(() => new ProcessIdentityVerifier(new FakeReader(ValidEvidence(), ValidEvidence() with { StartedAtUtc = DateTimeOffset.UnixEpoch.AddSeconds(2) })).Verify(42, Policy));
    }

    [Fact]
    public void Verify_propagates_permission_denied_without_falling_back()
    {
        Assert.Throws<UnauthorizedAccessException>(() => new ProcessIdentityVerifier(new ThrowingReader()).Verify(42, Policy));
    }

    private static WeixinProcessIdentityEvidence ValidEvidence() => new(
        42, "Weixin", DateTimeOffset.UnixEpoch, "C:\\Program Files\\Tencent\\Weixin.exe",
        new string('a', 64), "4.1.11.55", true, "Tencent Technology", "S-1-5-21-test", 7, "x64");

    private sealed class FakeReader(params WeixinProcessIdentityEvidence[] values) : IWeixinProcessIdentityReader
    {
        private int index;

        public WeixinProcessIdentityEvidence Read(int processId)
        {
            var value = values[Math.Min(index, values.Length - 1)];
            index++;
            return value;
        }
    }

    private sealed class ThrowingReader : IWeixinProcessIdentityReader
    {
        public WeixinProcessIdentityEvidence Read(int processId) => throw new UnauthorizedAccessException("access denied");
    }
}
