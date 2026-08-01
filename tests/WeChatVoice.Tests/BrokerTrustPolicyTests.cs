using System.Security.Cryptography;
using System.Text;
using WeChatVoice.Cli.Services.BrokerTrust;
using WeChatVoice.Windows;

namespace WeChatVoice.Tests;

public sealed class BrokerTrustPolicyTests
{
    [Fact]
    public void Development_policy_accepts_an_unsigned_broker_inside_the_repository_build_directory()
    {
        using var temporary = new TestTemporaryDirectory();
        var repository = temporary.CreateDirectory("repo");
        File.WriteAllText(Path.Combine(repository, DevelopmentBrokerTrustPolicy.SolutionFileName), string.Empty);
        var buildDirectory = temporary.CreateDirectory(Path.Combine("repo", "src", "WeChatVoice.KeyBroker", "bin", "Debug", "net10.0"));
        var broker = WriteFakeBroker(buildDirectory);

        var result = new DevelopmentBrokerTrustPolicy().Verify(broker);

        Assert.True(result.Verified);
    }

    [Fact]
    public void Development_policy_accepts_an_unsigned_broker_under_the_artifacts_directory()
    {
        using var temporary = new TestTemporaryDirectory();
        var repository = temporary.CreateDirectory("repo");
        File.WriteAllText(Path.Combine(repository, DevelopmentBrokerTrustPolicy.SolutionFileName), string.Empty);
        var buildDirectory = temporary.CreateDirectory(Path.Combine("repo", "artifacts", "cli-win-x64"));
        var broker = WriteFakeBroker(buildDirectory);

        var result = new DevelopmentBrokerTrustPolicy().Verify(broker);

        Assert.True(result.Verified);
    }

    [Fact]
    public void Development_policy_rejects_a_broker_outside_any_verified_repository()
    {
        using var temporary = new TestTemporaryDirectory();
        var elsewhere = temporary.CreateDirectory("elsewhere");
        var broker = WriteFakeBroker(elsewhere);

        var result = new DevelopmentBrokerTrustPolicy().Verify(broker);

        Assert.False(result.Verified);
        Assert.Equal("broker-outside-verified-repository", result.NonSensitiveReason);
    }

    [Fact]
    public void Development_policy_rejects_a_broker_inside_a_repository_but_outside_a_build_directory()
    {
        using var temporary = new TestTemporaryDirectory();
        var repository = temporary.CreateDirectory("repo");
        File.WriteAllText(Path.Combine(repository, DevelopmentBrokerTrustPolicy.SolutionFileName), string.Empty);
        var broker = WriteFakeBroker(temporary.CreateDirectory(Path.Combine("repo", "src", "other")));

        var result = new DevelopmentBrokerTrustPolicy().Verify(broker);

        Assert.False(result.Verified);
        Assert.Equal("broker-not-in-repository-build-directory", result.NonSensitiveReason);
    }

    [Fact]
    public void Release_policy_rejects_an_unsigned_broker()
    {
        using var temporary = new TestTemporaryDirectory();
        var install = temporary.CreateDirectory("install");
        var broker = WriteFakeBroker(install);
        WriteBrokerBundle(install, broker, publisherThumbprint: "publisher", verifierThumbprint: null);

        var result = new ReleaseBrokerTrustPolicy(
            new FakeAuthenticodeVerifier(new AuthenticodeSignature(false, null, null)),
            install,
            _ => false).Verify(broker);

        Assert.False(result.Verified);
        Assert.Equal("broker-not-authenticode-signed", result.NonSensitiveReason);
    }

    [Fact]
    public void Release_policy_rejects_a_missing_or_unpinned_bundle_manifest()
    {
        using var temporary = new TestTemporaryDirectory();
        var install = temporary.CreateDirectory("install");
        var broker = WriteFakeBroker(install);

        var missing = new ReleaseBrokerTrustPolicy(
            new FakeAuthenticodeVerifier(new AuthenticodeSignature(true, "publisher", "publisher")),
            install,
            _ => false).Verify(broker);
        Assert.False(missing.Verified);
        Assert.Equal("broker-bundle-manifest-unavailable", missing.NonSensitiveReason);

        WriteBrokerBundle(install, broker, publisherThumbprint: null, verifierThumbprint: "publisher");
        var unpinned = new ReleaseBrokerTrustPolicy(
            new FakeAuthenticodeVerifier(new AuthenticodeSignature(true, "publisher", "publisher")),
            install,
            _ => false).Verify(broker);
        Assert.False(unpinned.Verified);
        Assert.Equal("broker-publisher-unpinned", unpinned.NonSensitiveReason);
    }

    [Fact]
    public void Release_policy_rejects_a_publisher_thumbprint_mismatch()
    {
        using var temporary = new TestTemporaryDirectory();
        var install = temporary.CreateDirectory("install");
        var broker = WriteFakeBroker(install);
        WriteBrokerBundle(install, broker, publisherThumbprint: "pinned-publisher", verifierThumbprint: "other-publisher");

        var result = new ReleaseBrokerTrustPolicy(
            new FakeAuthenticodeVerifier(new AuthenticodeSignature(true, "other-publisher", "publisher")),
            install,
            _ => false).Verify(broker);

        Assert.False(result.Verified);
        Assert.Equal("broker-publisher-mismatch", result.NonSensitiveReason);
    }

    [Fact]
    public void Release_policy_rejects_a_user_writable_install_directory()
    {
        using var temporary = new TestTemporaryDirectory();
        var install = temporary.CreateDirectory("install");
        var broker = WriteFakeBroker(install);
        WriteBrokerBundle(install, broker, publisherThumbprint: "publisher", verifierThumbprint: "publisher");

        var result = new ReleaseBrokerTrustPolicy(
            new FakeAuthenticodeVerifier(new AuthenticodeSignature(true, "publisher", "publisher")),
            install,
            _ => true).Verify(broker);

        Assert.False(result.Verified);
        Assert.Equal("install-directory-user-writable", result.NonSensitiveReason);
    }

    [Fact]
    public void Release_policy_accepts_a_signed_broker_with_matching_manifest_and_non_writable_directory()
    {
        using var temporary = new TestTemporaryDirectory();
        var install = temporary.CreateDirectory("install");
        var broker = WriteFakeBroker(install);
        WriteBrokerBundle(install, broker, publisherThumbprint: "publisher", verifierThumbprint: "publisher");

        var result = new ReleaseBrokerTrustPolicy(
            new FakeAuthenticodeVerifier(new AuthenticodeSignature(true, "publisher", "publisher")),
            install,
            _ => false).Verify(broker);

        Assert.True(result.Verified);
    }

    [Fact]
    public void Release_policy_verifies_bundle_sidecar_hashes()
    {
        using var temporary = new TestTemporaryDirectory();
        var install = temporary.CreateDirectory("install");
        var broker = WriteFakeBroker(install);
        var deps = temporary.WriteFile(Path.Combine("install", "WeChatVoice.KeyBroker.deps.json"), Encoding.UTF8.GetBytes("{ }"));
        WriteBrokerBundle(install, broker, publisherThumbprint: "publisher", verifierThumbprint: "publisher", depsPath: Path.GetFileName(deps), depsHash: Hash(File.ReadAllBytes(deps)));

        var ok = new ReleaseBrokerTrustPolicy(
            new FakeAuthenticodeVerifier(new AuthenticodeSignature(true, "publisher", "publisher")),
            install,
            _ => false).Verify(broker);
        Assert.True(ok.Verified);

        // Tamper with the sidecar: the manifest hash no longer matches.
        File.WriteAllText(deps, "tampered");
        var tampered = new ReleaseBrokerTrustPolicy(
            new FakeAuthenticodeVerifier(new AuthenticodeSignature(true, "publisher", "publisher")),
            install,
            _ => false).Verify(broker);
        Assert.False(tampered.Verified);
        Assert.Equal("broker-bundle-sidecar-mismatch", tampered.NonSensitiveReason);
    }

    private static string WriteFakeBroker(string directory)
    {
        var path = Path.Combine(directory, "WeChatVoice.KeyBroker.exe");
        File.WriteAllBytes(path, "fake broker payload"u8.ToArray());
        return path;
    }

    private static void WriteBrokerBundle(
        string installDirectory,
        string brokerPath,
        string? publisherThumbprint,
        string? verifierThumbprint,
        string? depsPath = null,
        string? depsHash = null)
    {
        var brokerHash = Hash(File.ReadAllBytes(brokerPath));
        var manifest = new BrokerBundleManifest(
            brokerHash,
            depsPath,
            depsHash,
            null,
            null,
            publisherThumbprint);
        File.WriteAllText(
            Path.Combine(installDirectory, "WeChatVoice.KeyBroker.bundle.json"),
            System.Text.Json.JsonSerializer.Serialize(manifest, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
    }

    private static string Hash(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class FakeAuthenticodeVerifier(AuthenticodeSignature signature) : IAuthenticodeVerifier
    {
        public AuthenticodeSignature Verify(string path) => signature;
    }
}
