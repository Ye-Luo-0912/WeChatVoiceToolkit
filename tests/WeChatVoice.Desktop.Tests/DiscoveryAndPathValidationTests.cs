using WeChatVoice.Desktop.Infrastructure;

namespace WeChatVoice.Desktop.Tests;

public sealed class DiscoveryAndPathValidationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "WeChatVoiceToolkit.DiscoveryTests",
        Guid.NewGuid().ToString("N"));

    public DiscoveryAndPathValidationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void Discovery_returns_all_accounts_in_deterministic_order_without_picking_one()
    {
        var root = Path.Combine(_root, "xwechat_files");
        var first = CreateSource(root, "wxid_beta_0000000000000002", "beta.db");
        var second = CreateSource(root, "wxid_alpha_0000000000000001", "alpha.db");
        Directory.SetLastWriteTimeUtc(first, DateTime.UtcNow.AddDays(-3));
        Directory.SetLastWriteTimeUtc(second, DateTime.UtcNow);

        var candidates = new WeixinDataSourceDiscovery().Discover([root]);

        Assert.Equal(2, candidates.Count);
        Assert.Equal(
            candidates.OrderBy(item => item.DbStoragePath, StringComparer.OrdinalIgnoreCase).Select(item => item.DbStoragePath),
            candidates.Select(item => item.DbStoragePath));
        Assert.All(candidates, candidate =>
        {
            Assert.True(candidate.IsSelectable);
            Assert.False(candidate.IsReparsePoint);
            Assert.Equal(1, candidate.DatabaseCount);
            Assert.StartsWith("wxid_", candidate.AccountCandidate, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Snapshot_path_preflight_rejects_overlap_and_non_empty_output()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "source")).FullName;
        var nestedOutput = Path.Combine(source, "snapshot");
        var nested = DesktopPathValidator.ValidateSnapshotPaths(source, nestedOutput);
        Assert.False(nested.IsValid);
        Assert.Contains("包含", nested.Error, StringComparison.Ordinal);

        var output = Directory.CreateDirectory(Path.Combine(_root, "output")).FullName;
        File.WriteAllText(Path.Combine(output, "existing.txt"), "existing");
        var nonEmpty = DesktopPathValidator.ValidateSnapshotPaths(source, output);
        Assert.False(nonEmpty.IsValid);
        Assert.Contains("为空", nonEmpty.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Resetting_source_keeps_install_scoped_environment_assessment()
    {
        var session = new ExportProjectSession();
        var assessment = new FakeEnvironmentWorkflow().Result;
        session.EnvironmentAssessment = assessment;

        session.ResetFromSource(Path.Combine(_root, "new-source"));

        Assert.Same(assessment, session.EnvironmentAssessment);
    }

    private static string CreateSource(string root, string account, string databaseName)
    {
        var storage = Directory.CreateDirectory(Path.Combine(root, account, "db_storage")).FullName;
        File.WriteAllBytes(Path.Combine(storage, databaseName), [1, 2, 3]);
        return storage;
    }
}
