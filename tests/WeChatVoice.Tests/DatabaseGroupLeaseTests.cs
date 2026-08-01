using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using WeChatVoice.Core.Errors;
using WeChatVoice.Core.Models;
using WeChatVoice.Infrastructure.Sqlite;
using WeChatVoice.KeyBroker;

namespace WeChatVoice.Tests;

public sealed class DatabaseGroupLeaseTests
{
    [Fact]
    public async Task Probe_and_broker_target_derive_the_same_group_fingerprint()
    {
        using var temporary = new TestTemporaryDirectory();
        var root = temporary.CreateDirectory("snapshot");
        var mainBytes = new byte[4096];
        var walBytes = new byte[512];
        var shmBytes = new byte[64];
        RandomNumberGenerator.Fill(mainBytes);
        RandomNumberGenerator.Fill(walBytes);
        RandomNumberGenerator.Fill(shmBytes);
        var mainPath = temporary.WriteFile(Path.Combine("snapshot", "message", "message_0.db"), mainBytes);
        temporary.WriteFile(Path.Combine("snapshot", "message", "message_0.db-wal"), walBytes);
        temporary.WriteFile(Path.Combine("snapshot", "message", "message_0.db-shm"), shmBytes);
        var manifest = new SnapshotManifest(
            root,
            root,
            DateTimeOffset.UtcNow,
            [
                new SnapshotFileRecord("message/message_0.db", mainBytes.LongLength, Hash(mainBytes), File.GetLastWriteTimeUtc(mainPath)),
                new SnapshotFileRecord("message/message_0.db-wal", walBytes.LongLength, Hash(walBytes), File.GetLastWriteTimeUtc(mainPath)),
                new SnapshotFileRecord("message/message_0.db-shm", shmBytes.LongLength, Hash(shmBytes), File.GetLastWriteTimeUtc(mainPath)),
            ]);
        var verified = new VerifiedRawSnapshot(new RawSnapshot(manifest, root), DateTimeOffset.UtcNow);

        var targets = await DatabaseGroupTarget.LoadAsync(verified, CancellationToken.None);
        var target = Assert.Single(targets);
        var probe = await new DataSetProbeService().ProbeAsync(root, new DataSetProbeOptions(), CancellationToken.None);
        var artifact = Assert.Single(probe.DataSet.Databases, static item => item.DatabasePath == "message/message_0.db");

        Assert.Equal(artifact.DatabaseGroupFingerprint, target.DatabaseGroupFingerprint);
    }

    [Fact]
    public async Task VerifiedDatabaseGroupLease_accepts_an_unchanged_group_and_rejects_a_changed_wal()
    {
        using var temporary = new TestTemporaryDirectory();
        var root = temporary.CreateDirectory("snapshot");
        var mainBytes = new byte[4096];
        var walBytes = new byte[512];
        var shmBytes = new byte[64];
        RandomNumberGenerator.Fill(mainBytes);
        RandomNumberGenerator.Fill(walBytes);
        RandomNumberGenerator.Fill(shmBytes);
        var mainPath = temporary.WriteFile(Path.Combine("snapshot", "message", "message_0.db"), mainBytes);
        temporary.WriteFile(Path.Combine("snapshot", "message", "message_0.db-wal"), walBytes);
        temporary.WriteFile(Path.Combine("snapshot", "message", "message_0.db-shm"), shmBytes);
        var manifest = new SnapshotManifest(
            root,
            root,
            DateTimeOffset.UtcNow,
            [
                new SnapshotFileRecord("message/message_0.db", mainBytes.LongLength, Hash(mainBytes), File.GetLastWriteTimeUtc(mainPath)),
                new SnapshotFileRecord("message/message_0.db-wal", walBytes.LongLength, Hash(walBytes), File.GetLastWriteTimeUtc(mainPath)),
                new SnapshotFileRecord("message/message_0.db-shm", shmBytes.LongLength, Hash(shmBytes), File.GetLastWriteTimeUtc(mainPath)),
            ]);
        var verified = new VerifiedRawSnapshot(new RawSnapshot(manifest, root), DateTimeOffset.UtcNow);
        var target = Assert.Single(await DatabaseGroupTarget.LoadAsync(verified, CancellationToken.None));

        await using (var lease = await VerifiedDatabaseGroupLease.OpenAsync(mainPath, target, CancellationToken.None))
        {
            Assert.Equal(mainPath, lease.MainPath);
        }

        // Tamper with the WAL after the snapshot was staged: the group must be
        // rejected through the open handles on the next open.
        File.WriteAllBytes(mainPath + "-wal", new byte[1024]);
        var failure = await Assert.ThrowsAsync<AppFailureException>(
            () => VerifiedDatabaseGroupLease.OpenAsync(mainPath, target, CancellationToken.None));
        Assert.Equal(ErrorCode.SnapshotInconsistent, failure.Code);
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void BrokerDirectorySecurity_restricts_a_directory_to_system_and_administrators()
    {
        using var temporary = new TestTemporaryDirectory();
        var directory = temporary.CreateDirectory("restricted");

        BrokerDirectorySecurity.RestrictToSystemAndAdministrators(directory);

        var info = new DirectoryInfo(directory);
        var security = info.GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);
        var rules = security.GetAccessRules(true, false, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        Assert.Contains(rules, rule => rule.IdentityReference == system
            && rule.FileSystemRights == FileSystemRights.FullControl
            && rule.AccessControlType == AccessControlType.Allow);
        Assert.Contains(rules, rule => rule.IdentityReference == administrators
            && rule.FileSystemRights == FileSystemRights.FullControl
            && rule.AccessControlType == AccessControlType.Allow);
        Assert.DoesNotContain(rules, rule => rule.IdentityReference.Value.EndsWith("\\Everyone", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(rules, rule => rule.IdentityReference.Value.EndsWith("\\Users", StringComparison.OrdinalIgnoreCase));

        // Restore inherited access so the non-elevated test process can clean
        // up the directory it created.
        security.SetAccessRuleProtection(isProtected: false, preserveInheritance: true);
        info.SetAccessControl(security);
    }

    private static string Hash(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
