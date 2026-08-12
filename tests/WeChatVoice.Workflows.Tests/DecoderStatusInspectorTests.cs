using WeChatVoice.Core.Models;
using WeChatVoice.Infrastructure.Audio;

namespace WeChatVoice.Workflows.Tests;

public sealed class DecoderStatusInspectorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WeChatVoiceToolkit.WorkflowsTests", Guid.NewGuid().ToString("N"));

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
    public void Decoder_config_store_round_trips_and_clears_worker_path()
    {
        var store = new DecoderConfigurationStore(_root);
        Assert.Null(store.LoadWorkerPath());

        var workerPath = Path.Combine(_root, "decoder-worker.exe");
        store.SetWorkerPath(workerPath);

        Assert.Equal(Path.GetFullPath(workerPath), store.LoadWorkerPath());

        store.SetWorkerPath(null);
        Assert.Null(store.LoadWorkerPath());
    }

    [Fact]
    public void Inspector_reports_missing_when_nothing_is_configured()
    {
        var inspector = new DecoderStatusInspector(new DecoderConfigurationStore(_root), environment: () => null);

        var report = inspector.Report();

        Assert.Equal(DecoderStatus.Missing, report.Status);
        Assert.False(inspector.IsDurationAnalysisAvailable());
    }

    [Fact]
    public void Inspector_reports_failed_self_test_when_configured_path_does_not_exist()
    {
        var store = new DecoderConfigurationStore(_root);
        store.SetWorkerPath(Path.Combine(_root, "missing-decoder.exe"));
        var inspector = new DecoderStatusInspector(store, environment: () => null);

        var report = inspector.Report();

        Assert.Equal(DecoderStatus.FailedSelfTest, report.Status);
    }

    [Fact]
    public void Inspector_reports_available_and_prefers_persisted_over_environment()
    {
        Directory.CreateDirectory(_root);
        var store = new DecoderConfigurationStore(_root);
        var persisted = Path.Combine(_root, "persisted-worker.exe");
        var environmentPath = Path.Combine(_root, "env-worker.exe");
        File.WriteAllBytes(persisted, [1, 2, 3]);
        store.SetWorkerPath(persisted);

        var inspector = new DecoderStatusInspector(store, environment: () => environmentPath);

        Assert.Equal(Path.GetFullPath(persisted), inspector.DiscoverWorkerPath());
        var report = inspector.Report();
        Assert.Equal(DecoderStatus.Available, report.Status);
        Assert.True(inspector.IsDurationAnalysisAvailable());
    }

    [Fact]
    public void Inspector_falls_back_to_environment_when_persisted_is_empty()
    {
        Directory.CreateDirectory(_root);
        var store = new DecoderConfigurationStore(_root);
        var environmentPath = Path.Combine(_root, "env-worker.exe");
        File.WriteAllBytes(environmentPath, [1, 2, 3]);

        var inspector = new DecoderStatusInspector(store, environment: () => environmentPath);

        Assert.Equal(Path.GetFullPath(environmentPath), inspector.DiscoverWorkerPath());
        Assert.Equal(DecoderStatus.Available, inspector.Report().Status);
    }
}
