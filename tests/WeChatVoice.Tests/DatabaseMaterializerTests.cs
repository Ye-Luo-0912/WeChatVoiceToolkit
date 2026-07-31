using WeChatVoice.Core.Models;
using WeChatVoice.Infrastructure.Materialization;

namespace WeChatVoice.Tests;

public sealed class DatabaseMaterializerTests
{
    [Fact]
    public async Task MaterializeAsync_rejects_raw_snapshot_when_manifest_hash_is_wrong()
    {
        using var temporary = new TestTemporaryDirectory();
        var snapshotRoot = temporary.CreateDirectory("snapshot");
        var database = temporary.WriteFile(Path.Combine("snapshot", "message_0.db"), [1, 2, 3]);
        var manifest = new SnapshotManifest(
            snapshotRoot,
            snapshotRoot,
            DateTimeOffset.UtcNow,
            [new SnapshotFileRecord("message_0.db", new FileInfo(database).Length, new string('0', 64), DateTimeOffset.UtcNow)]);
        var rawSnapshot = new RawSnapshot(manifest);
        var output = temporary.GetPath("materialized");
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("The test process path is unavailable.");

        var exception = await Assert.ThrowsAsync<DatabaseMaterializationException>(() => new ExternalDatabaseMaterializer(executable).MaterializeAsync(
            rawSnapshot,
            new MaterializationOptions(Path.GetFullPath(output)),
            CancellationToken.None));

        Assert.Contains("failed manifest verification", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(output));
    }
}
