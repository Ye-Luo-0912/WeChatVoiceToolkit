using System.Text.Json;
using WeChatVoice.Core.Models;
using WeChatVoice.Infrastructure.Sqlite;

namespace WeChatVoice.Tests;

public sealed class DataSetProbeTests
{
    [Fact]
    public async Task ProbeAsync_discovers_shards_pairs_and_redacts_local_paths_by_default()
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
        Assert.Contains(probe.Issues, issue => issue.Code == "missing-media-shard");
        Assert.All(probe.DataSet.Databases, artifact => Assert.False(Path.IsPathRooted(artifact.DatabasePath)));
        Assert.All(probe.DataSet.Databases, artifact => Assert.Null(artifact.LocalPath));
        Assert.All(probe.DataSet.Databases, artifact => Assert.False(string.IsNullOrWhiteSpace(artifact.Schema.SchemaFingerprint)));
        Assert.All(probe.DataSet.Databases, artifact => Assert.False(string.IsNullOrWhiteSpace(artifact.Sha256)));

        var json = JsonSerializer.Serialize(probe);
        var roundTrip = JsonSerializer.Deserialize<DataSetProbe>(json);
        Assert.NotNull(roundTrip);
        Assert.Equal(probe.DataSet.DataSetId, roundTrip!.DataSet.DataSetId);
    }
}
