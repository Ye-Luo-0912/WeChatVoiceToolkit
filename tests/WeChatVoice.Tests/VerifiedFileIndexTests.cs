using WeChatVoice.Core.Models;
using WeChatVoice.Infrastructure.Serialization;
using WeChatVoice.Infrastructure.Sqlite;

namespace WeChatVoice.Tests;

public sealed class VerifiedFileIndexTests
{
    [Fact]
    public async Task FileIndexBuilder_hashes_every_file_once_and_matches_direct_hashing()
    {
        using var temporary = new TestTemporaryDirectory();
        var root = temporary.CreateDirectory("root");
        var first = temporary.WriteFile(Path.Combine("root", "databases", "message_0.db"), Enumerable.Repeat((byte)1, 4096).ToArray());
        var second = temporary.WriteFile(Path.Combine("root", "databases", "message_0.db-wal"), Enumerable.Repeat((byte)2, 256).ToArray());

        var index = await FileIndexBuilder.BuildAsync(root, CancellationToken.None);

        Assert.Equal(2, index.Entries.Count);
        Assert.True(index.TryGet("databases/message_0.db", out var main));
        Assert.Equal(4096, main.ByteLength);
        Assert.Equal(await FileHashing.ComputeSha256Async(first, CancellationToken.None), main.Sha256);
        Assert.True(index.TryGet("databases/message_0.db-wal", out var wal));
        Assert.Equal(256, wal.ByteLength);
        Assert.Equal(await FileHashing.ComputeSha256Async(second, CancellationToken.None), wal.Sha256);
    }

    [Fact]
    public async Task Probe_with_a_prebuilt_index_matches_probe_without_one()
    {
        using var temporary = new TestTemporaryDirectory();
        var root = temporary.CreateDirectory("root");
        temporary.WriteFile(Path.Combine("root", "databases", "message_0.db"), Enumerable.Repeat((byte)3, 4096).ToArray());
        temporary.WriteFile(Path.Combine("root", "databases", "message_0.db-shm"), Enumerable.Repeat((byte)4, 64).ToArray());

        var index = await FileIndexBuilder.BuildAsync(root, CancellationToken.None);
        var withIndex = await new DataSetProbeService().ProbeAsync(root, new DataSetProbeOptions(), index, CancellationToken.None);
        var withoutIndex = await new DataSetProbeService().ProbeAsync(root, new DataSetProbeOptions(), CancellationToken.None);

        Assert.Equal(withoutIndex.DataSet.DataSetId, withIndex.DataSet.DataSetId);
        var expected = Assert.Single(withoutIndex.DataSet.Databases, static item => item.DatabasePath == "databases/message_0.db");
        var actual = Assert.Single(withIndex.DataSet.Databases, static item => item.DatabasePath == "databases/message_0.db");
        Assert.Equal(expected.DatabaseGroupFingerprint, actual.DatabaseGroupFingerprint);
        Assert.Equal(expected.MainSha256, actual.MainSha256);
        Assert.Equal(expected.ShmSha256, actual.ShmSha256);
    }
}
