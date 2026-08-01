using WeChatVoice.Core.Models;

namespace WeChatVoice.Tests;

public sealed class SnapshotSourceIdentityTests
{
    [Fact]
    public void TryDerive_extracts_the_candidate_and_anchors_the_layout_from_verified_files()
    {
        var root = Path.Combine("C:\\Users\\me\\WeChat Files", "wxid_abc123_1a2b", "db_storage");
        var files = new[]
        {
            new SnapshotFileRecord("msg/other.db", 1, "a", DateTimeOffset.UtcNow, FileId: "win:0000:0000"),
            new SnapshotFileRecord("media_0.db", 2, "b", DateTimeOffset.UtcNow, FileId: "win:1234:5678"),
            new SnapshotFileRecord("contact.db", 3, "c", DateTimeOffset.UtcNow, FileId: "win:1234:9999"),
        };

        var identity = SnapshotSourceIdentity.TryDerive(root, files);

        Assert.NotNull(identity);
        Assert.Equal("wxid_abc123", identity.AccountCandidate);
        Assert.Equal("wxid_abc123_1a2b", identity.AccountDirectoryName);
        Assert.Equal(1, identity.SourceLayoutVersion);
        Assert.Equal("win:1234:9999", identity.SourceRootFileId);
    }

    [Theory]
    [InlineData("C:\\data")]
    [InlineData("C:\\data\\wxid_plain")]
    [InlineData("C:\\data\\wxid_abc_zzzz\\db_storage")]
    [InlineData("C:\\data\\other_abc_1a2b\\db_storage")]
    [InlineData("C:\\data\\wxid_abc_1a2b\\other")]
    public void TryDerive_rejects_unrecognized_layouts(string sourceDirectory)
    {
        Assert.Null(SnapshotSourceIdentity.TryDerive(sourceDirectory, Array.Empty<SnapshotFileRecord>()));
    }

    [Fact]
    public void TryDerive_returns_null_when_no_manifest_file_anchors_the_account_directory()
    {
        var root = Path.Combine("C:\\data", "wxid_abc_1a2b", "db_storage");
        var identity = SnapshotSourceIdentity.TryDerive(root, Array.Empty<SnapshotFileRecord>());

        Assert.NotNull(identity);
        Assert.Equal("wxid_abc", identity.AccountCandidate);
        Assert.Null(identity.SourceRootFileId);
    }
}
