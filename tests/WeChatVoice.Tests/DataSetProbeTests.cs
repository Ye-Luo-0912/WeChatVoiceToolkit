using System.Text.Json;
using WeChatVoice.Core.Models;
using WeChatVoice.Infrastructure.Adapters;
using WeChatVoice.Infrastructure.Sqlite;
using WeChatVoice.Workflows.Workspaces;

namespace WeChatVoice.Tests;

public sealed class DataSetProbeTests
{
    [Fact]
    public async Task ProbeAsync_discovers_shards_without_assuming_message_media_filename_parity()
    {
        using var temporary = new TestTemporaryDirectory();
        var root = temporary.CreateDirectory("decrypted-db");
        await SqliteSchemaInspectorTests.CreateSampleDatabaseAsync(Path.Combine(root, "message_0.db"));
        await SqliteSchemaInspectorTests.CreateSampleDatabaseAsync(Path.Combine(root, "media_0.db"));
        await SqliteSchemaInspectorTests.CreateSampleDatabaseAsync(Path.Combine(root, "message_1.db"));

        var probe = await new DataSetProbeService().ProbeAsync(root, new DataSetProbeOptions(), CancellationToken.None);

        Assert.Equal(3, probe.DataSet.Databases.Count);
        Assert.Contains(probe.DataSet.Databases, artifact => artifact.LogicalRole == "message" && artifact.ShardNumber == 0);
        Assert.Contains(probe.DataSet.Databases, artifact => artifact.LogicalRole == "media" && artifact.ShardNumber == 0);
        Assert.Contains(probe.Issues, issue => issue.Code == "unverified-shard-topology" && issue.Severity == "info");
        Assert.DoesNotContain(probe.Issues, issue => issue.Code is "missing-media-database" or "missing-message-database");
        Assert.All(probe.DataSet.Databases, artifact => Assert.False(Path.IsPathRooted(artifact.DatabasePath)));
        Assert.All(probe.DataSet.Databases, artifact => Assert.Null(artifact.LocalPath));
        Assert.All(probe.DataSet.Databases, artifact => Assert.False(string.IsNullOrWhiteSpace(artifact.Schema.SchemaFingerprint)));
        Assert.All(probe.DataSet.Databases, artifact =>
        {
            Assert.False(string.IsNullOrWhiteSpace(artifact.MainSha256));
            Assert.True(artifact.MainLength > 0);
            Assert.False(string.IsNullOrWhiteSpace(artifact.DatabaseGroupFingerprint));
        });
        var candidate = Assert.Single(probe.AdapterCandidates);
        Assert.Equal("weixin-windows-4", candidate.AdapterId);
        Assert.False(candidate.IsMatch);

        var json = JsonSerializer.Serialize(probe);
        var roundTrip = JsonSerializer.Deserialize<DataSetProbe>(json);
        Assert.NotNull(roundTrip);
        Assert.Equal(probe.DataSet.DataSetId, roundTrip!.DataSet.DataSetId);
    }

    [Theory]
    [InlineData("message_0.db", "missing-media-database")]
    [InlineData("media_0.db", "missing-message-database")]
    public async Task ProbeAsync_requires_message_and_media_roles_without_guessing_shard_pairs(string databaseName, string expectedIssue)
    {
        using var temporary = new TestTemporaryDirectory();
        var root = temporary.CreateDirectory("decrypted-db");
        await SqliteSchemaInspectorTests.CreateSampleDatabaseAsync(Path.Combine(root, databaseName));

        var probe = await new DataSetProbeService().ProbeAsync(root, new DataSetProbeOptions(), CancellationToken.None);

        Assert.Contains(probe.Issues, issue => issue.Code == expectedIssue && issue.Severity == "error");
        Assert.DoesNotContain(probe.Issues, issue => issue.Code == "unverified-shard-topology");
    }

    [Fact]
    public async Task LocalWorkspaceCreator_retains_absolute_paths_separately_from_shareable_probe()
    {
        using var temporary = new TestTemporaryDirectory();
        var root = temporary.CreateDirectory("decrypted-db");
        var databasePath = Path.Combine(root, "message_0.db");
        await SqliteSchemaInspectorTests.CreateSampleDatabaseAsync(databasePath);

        var workspace = await new LocalWorkspaceCreator(new DataSetProbeService(adapters: BuiltInAdapters.Create()))
            .CreateAsync(root, CancellationToken.None);

        Assert.True(Path.IsPathFullyQualified(workspace.SourceRoot));
        Assert.Equal(Path.GetFullPath(root), workspace.SourceRoot);
        Assert.All(workspace.DataSet.Databases, artifact => Assert.True(artifact.LocalPath is { } localPath && Path.IsPathFullyQualified(localPath)));
        Assert.Equal("weixin-windows-4", Assert.Single(workspace.AdapterCandidates).AdapterId);
    }

    [Fact]
    public async Task LocalWorkspaceDocumentStore_round_trips_user_confirmation_without_upgrading_evidence()
    {
        using var temporary = new TestTemporaryDirectory();
        var root = temporary.CreateDirectory("decrypted-db");
        await SqliteSchemaInspectorTests.CreateSampleDatabaseAsync(Path.Combine(root, "message_0.db"));

        var workspace = await new LocalWorkspaceCreator().CreateAsync(root, CancellationToken.None);
        var persisted = workspace.WithAccountIdentity(new AccountIdentity(
            AccountIdentityState.Candidate,
            ConfirmedBy: null,
            UserConfirmationState.Confirmed));
        var path = temporary.GetPath("workspace", "local-workspace.json");

        await LocalWorkspaceDocumentStore.WriteAsync(path, persisted, CancellationToken.None);
        var loaded = await new WorkspaceLoader().ReadAsync(path, CancellationToken.None);

        Assert.Equal(AccountIdentityState.Candidate, loaded.AccountIdentity.State);
        Assert.Equal(UserConfirmationState.Confirmed, loaded.AccountIdentity.UserConfirmation);
        Assert.Null(loaded.AccountIdentity.ConfirmedBy);
        Assert.Equal(persisted.WorkspaceId, loaded.WorkspaceId);
        Assert.Equal(persisted.SourceRoot, loaded.SourceRoot);
    }

    [Fact]
    public async Task LocalWorkspaceVerifier_rejects_database_content_changes_after_creation()
    {
        using var temporary = new TestTemporaryDirectory();
        var root = temporary.CreateDirectory("decrypted-db");
        var databasePath = Path.Combine(root, "message_0.db");
        await SqliteSchemaInspectorTests.CreateSampleDatabaseAsync(databasePath);

        var workspace = await new LocalWorkspaceCreator().CreateAsync(root, CancellationToken.None);
        var verified = await new LocalWorkspaceVerifier().VerifyAsync(workspace, CancellationToken.None);
        Assert.Equal(workspace.WorkspaceId, verified.Workspace.WorkspaceId);

        await File.AppendAllBytesAsync(databasePath, [0x7F]);
        var exception = await Assert.ThrowsAsync<WorkspaceVerificationException>(() => new LocalWorkspaceVerifier().VerifyAsync(workspace, CancellationToken.None));
        Assert.Contains("no longer matches", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProbeAsync_rejects_snapshot_manifest_hash_mismatch_instead_of_trusting_it()
    {
        using var temporary = new TestTemporaryDirectory();
        var root = temporary.CreateDirectory("decrypted-db");
        var databasePath = Path.Combine(root, "message_0.db");
        await SqliteSchemaInspectorTests.CreateSampleDatabaseAsync(databasePath);
        var fakeHash = new string('a', 64);
        var manifest = new SnapshotManifest(
            root,
            root,
            DateTimeOffset.UtcNow,
            [new SnapshotFileRecord("message_0.db", new FileInfo(databasePath).Length, fakeHash, DateTimeOffset.UtcNow)]);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => new DataSetProbeService().ProbeAsync(
            root,
            new DataSetProbeOptions(SnapshotManifest: manifest),
            CancellationToken.None));

        Assert.Contains("does not match", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProbeAsync_reports_encrypted_or_non_sqlite_database_without_attempting_decryption()
    {
        using var temporary = new TestTemporaryDirectory();
        var root = temporary.CreateDirectory("decrypted-db");
        var mainPath = Path.Combine(root, "message_0.db");
        await File.WriteAllBytesAsync(mainPath,
            Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray());
        await File.WriteAllBytesAsync(mainPath + "-wal", [0xAA, 0xBB, 0xCC]);
        await File.WriteAllBytesAsync(mainPath + "-shm", [0xDD, 0xEE]);

        var probe = await new DataSetProbeService().ProbeAsync(root, new DataSetProbeOptions(), CancellationToken.None);

        var issue = Assert.Single(probe.Issues, item => item.Code == "encrypted-or-non-sqlite");
        Assert.Contains("No decryption is attempted", issue.Message, StringComparison.Ordinal);
        Assert.Equal("message", probe.DataSet.Databases[0].LogicalRole);
        Assert.False(string.IsNullOrWhiteSpace(probe.DataSet.Databases[0].MainSha256));
        Assert.Equal(3, probe.DataSet.Databases[0].WalLength);
        Assert.Equal(2, probe.DataSet.Databases[0].ShmLength);
        Assert.False(string.IsNullOrWhiteSpace(probe.DataSet.Databases[0].WalSha256));
        Assert.False(string.IsNullOrWhiteSpace(probe.DataSet.Databases[0].ShmSha256));
    }
}
