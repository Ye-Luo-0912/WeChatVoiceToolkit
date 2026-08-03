using WeChatVoice.Core.Models;
using WeChatVoice.Infrastructure.Serialization;
using WeChatVoice.Infrastructure.Sqlite;

namespace WeChatVoice.Tests;

public sealed class VerifiedWorkspaceFileLeaseTests
{
    [Fact]
    public async Task Lease_rejects_a_new_sqlite_sidecar_after_initial_verification()
    {
        using var temporary = new TestTemporaryDirectory();
        var root = temporary.CreateDirectory("workspace");
        var databasePath = Path.Combine(root, "database.db");
        await File.WriteAllBytesAsync(databasePath, [1, 2, 3, 4]);
        var info = new FileInfo(databasePath);
        var hash = await FileHashing.ComputeSha256Async(databasePath, CancellationToken.None);
        var artifact = new DatabaseArtifact(
            "message",
            0,
            "database.db",
            hash,
            new SchemaSnapshot("database.db", DateTimeOffset.UtcNow),
            databasePath,
            MainLength: info.Length);
        var workspace = new LocalWorkspace(
            "workspace-id",
            root,
            new WeChatDataSet("dataset-id", "account-id", [artifact]),
            DateTimeOffset.UtcNow);

        await using var lease = await VerifiedWorkspaceFileLease.OpenAsync(
            new VerifiedLocalWorkspace(workspace, DateTimeOffset.UtcNow),
            CancellationToken.None);

        await File.WriteAllBytesAsync(databasePath + "-wal", [9, 8, 7]);

        var exception = await Assert.ThrowsAsync<WorkspaceVerificationException>(
            () => lease.VerifyAsync(CancellationToken.None));
        Assert.Contains("sidecar", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
