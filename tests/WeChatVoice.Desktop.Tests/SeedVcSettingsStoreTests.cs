using WeChatVoice.Desktop.Infrastructure;

namespace WeChatVoice.Desktop.Tests;

public sealed class SeedVcSettingsStoreTests
{
    [Fact]
    public void Settings_are_replaced_by_dataset_fingerprint_and_survive_reopen()
    {
        using var temporary = new TemporaryDirectory();
        var store = new SeedVcSettingsStore(temporary.Root);
        store.Save(new SeedVcSettings("dataset-a", SeedVcRoot: "D:/seed-vc", PrepDirectory: "D:/prep-a"));
        store.Save(new SeedVcSettings("dataset-a", SeedVcRoot: "D:/seed-vc-2", CheckpointPath: "D:/run/ft_model.pth"));
        store.Save(new SeedVcSettings("dataset-b", SeedVcRoot: "D:/other"));

        var reopened = new SeedVcSettingsStore(temporary.Root);
        var a = reopened.Load("dataset-a");
        Assert.NotNull(a);
        Assert.Equal("D:/seed-vc-2", a.SeedVcRoot);
        Assert.Equal("D:/run/ft_model.pth", a.CheckpointPath);
        Assert.Null(reopened.Load("missing"));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Root = Path.Combine(Path.GetTempPath(), "WeChatVoiceToolkit.SeedVcSettings", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
