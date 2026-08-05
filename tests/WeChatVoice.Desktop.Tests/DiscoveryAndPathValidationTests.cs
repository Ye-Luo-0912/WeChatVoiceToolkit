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
    public void Discovery_uses_configured_data_root_when_no_root_is_given()
    {
        var root = Path.Combine(_root, "configured-xwechat-files");
        var source = CreateSource(root, "wxid_configured_0000000000000001", "message_0.db");
        var previous = Environment.GetEnvironmentVariable("WECHATVOICE_WEIXIN_DATA_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("WECHATVOICE_WEIXIN_DATA_ROOT", root);

            var candidate = new WeixinDataSourceDiscovery()
                .Discover()
                .First(item => string.Equals(item.DbStoragePath, source, StringComparison.OrdinalIgnoreCase));

            Assert.Equal(source, candidate.DbStoragePath);
            Assert.Equal("wxid_configured", candidate.AccountCandidate);
        }
        finally
        {
            Environment.SetEnvironmentVariable("WECHATVOICE_WEIXIN_DATA_ROOT", previous);
        }
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
    public void Discovery_counts_nested_databases_and_uses_recent_snapshot_store()
    {
        var root = Path.Combine(_root, "xwechat_files");
        var source = CreateSource(root, "wxid_nested_0000000000000001", "top.db");
        var nested = Directory.CreateDirectory(Path.Combine(source, "message", "shard-0")).FullName;
        File.WriteAllBytes(Path.Combine(nested, "nested.db"), [1, 2, 3]);
        var snapshot = Directory.CreateDirectory(Path.Combine(_root, "snapshots", "one")).FullName;
        var recent = new RecentWorkspaceStore(Path.Combine(_root, "recent"));
        recent.AddSnapshot(source, snapshot, "snapshot-one");

        var candidate = Assert.Single(new WeixinDataSourceDiscovery(recent).Discover([root]));

        Assert.Equal(2, candidate.DatabaseCount);
        Assert.True(candidate.HasSnapshot);
        Assert.Equal("wxid_nested", candidate.AccountCandidate);
    }

    [Fact]
    public async Task Discovery_honors_directory_budget_and_cancellation()
    {
        var root = Directory.CreateDirectory(Path.Combine(_root, "bounded")).FullName;
        Directory.CreateDirectory(Path.Combine(root, "a", "b", "c"));

        var result = await new WeixinDataSourceDiscovery().DiscoverDetailedAsync(
            [root],
            new WeixinDataSourceDiscoveryOptions(MaxDepth: 20, MaxDirectories: 1, Timeout: TimeSpan.FromSeconds(1)),
            CancellationToken.None);
        Assert.True(result.WasTruncated);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new WeixinDataSourceDiscovery().DiscoverAsync([root], cancellationToken: cancellation.Token));
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

    [Fact]
    public void Default_snapshot_output_is_opaque_unique_and_disjoint_from_source()
    {
        var source = CreateSource(Path.Combine(_root, "source-root"), "wxid_private_0000000000000010", "source.db");
        var factory = new SnapshotOutputDirectoryFactory(_root);

        var first = factory.CreateDefault(source, "wxid_private", Path.GetDirectoryName(source)!);
        var second = factory.CreateDefault(source, "wxid_private", Path.GetDirectoryName(source)!);

        Assert.True(Path.IsPathFullyQualified(first));
        Assert.True(Path.IsPathFullyQualified(second));
        Assert.NotEqual(first, second);
        Assert.DoesNotContain("wxid_private", first, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("wxid_private", second, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(first));
        Assert.False(Directory.Exists(second));
        Assert.False(first.StartsWith(source, StringComparison.OrdinalIgnoreCase));
        Assert.False(source.StartsWith(first, StringComparison.OrdinalIgnoreCase));
    }

    private static string CreateSource(string root, string account, string databaseName)
    {
        var storage = Directory.CreateDirectory(Path.Combine(root, account, "db_storage")).FullName;
        File.WriteAllBytes(Path.Combine(storage, databaseName), [1, 2, 3]);
        return storage;
    }
}
