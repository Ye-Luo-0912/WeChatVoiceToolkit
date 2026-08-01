using WeChatVoice.Desktop.Infrastructure;

namespace WeChatVoice.Desktop.Tests;

public sealed class RecentWorkspaceAndLogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WeChatVoiceToolkit.DesktopTests", Guid.NewGuid().ToString("N"));

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
    public void Recent_store_round_trips_and_dedupes_by_path()
    {
        var store = new RecentWorkspaceStore(_root);
        var workspace = TestDoubles.Verified();
        var path = Path.Combine(_root, "w", "workspace.json");

        store.Add(workspace, path);
        store.Add(workspace, path);

        var entries = store.Load();
        var entry = Assert.Single(entries);
        Assert.Equal(Path.GetFullPath(path), entry.WorkspacePath);
        Assert.Equal("workspace-fake", entry.WorkspaceId);
    }

    [Fact]
    public void Recent_store_remove_works()
    {
        var store = new RecentWorkspaceStore(_root);
        var workspace = TestDoubles.Verified();
        var path = Path.Combine(_root, "w", "workspace.json");

        store.Add(workspace, path);
        store.Remove(path);

        Assert.Empty(store.Load());
    }

    [Fact]
    public void Log_scrubs_wxid_and_long_hex()
    {
        var log = new DesktopLog(_root);
        log.Info("account wxid_sto5zbw1l3jk21 key ac599744a7ce7b65640ebe18c939c0d4e4a06cd039d89cddee7f1e9afc56875d");

        var text = File.ReadAllText(log.LogPath);
        Assert.DoesNotContain("sto5zbw1l3jk21", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ac599744", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Log_records_typed_stages_and_error_codes()
    {
        var log = new DesktopLog(_root);
        log.Stage(Core.Models.OperationPhase.VoiceScan, Core.Models.OperationStageIds.QueryingVoices, 42);
        log.ErrorCode(Core.Errors.ErrorCode.WorkerFailed);

        var text = File.ReadAllText(log.LogPath);
        Assert.Contains("stage VoiceScan:querying-voices 42%", text, StringComparison.Ordinal);
        Assert.Contains("error-code WorkerFailed", text, StringComparison.Ordinal);
    }
}
