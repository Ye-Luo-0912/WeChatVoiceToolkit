using WeChatVoice.Workflows.Workflows;

namespace WeChatVoice.Workflows.Tests;

public sealed class SnapshotWorkflowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WeChatVoiceToolkit.WorkflowTests", Guid.NewGuid().ToString("N"));

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
    public async Task Creates_snapshot_and_reports_file_count_and_manifest_path()
    {
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(Path.Combine(source, "media"));
        await File.WriteAllBytesAsync(Path.Combine(source, "media", "voice.db"), [1, 2, 3, 4]);
        var output = Path.Combine(_root, "snapshot");

        var context = new WorkflowContext(new TestAccountConfirmation(confirmed: false));
        var result = await new SnapshotWorkflow().RunAsync(
            new SnapshotWorkflowRequest(source, output, AllowLiveSource: true, MaxAttempts: 1),
            context,
            CancellationToken.None);

        Assert.Single(result.Manifest.Files);
        Assert.Equal(output, result.Manifest.SnapshotDirectory);
        Assert.True(File.Exists(result.ManifestPath));
        Assert.Equal(WorkflowState.Completed, context.StateMachine.State);
    }

    [Fact]
    public async Task Derives_account_candidate_from_verified_source_layout()
    {
        var source = Path.Combine(_root, "wxid_testuser_abcd", "db_storage");
        Directory.CreateDirectory(source);
        await File.WriteAllBytesAsync(Path.Combine(source, "media.db"), [5]);
        var output = Path.Combine(_root, "snapshot");

        var context = new WorkflowContext(new TestAccountConfirmation(confirmed: false));
        var result = await new SnapshotWorkflow().RunAsync(
            new SnapshotWorkflowRequest(source, output, AllowLiveSource: true, MaxAttempts: 1),
            context,
            CancellationToken.None);

        Assert.Equal("wxid_testuser", result.SourceIdentity?.AccountCandidate);
    }

    [Fact]
    public async Task Cancellation_transitions_to_cancelled()
    {
        using var cts = new CancellationTokenSource();
        var context = new WorkflowContext(new TestAccountConfirmation(confirmed: false));
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new SnapshotWorkflow().RunAsync(
                new SnapshotWorkflowRequest(Path.Combine(_root, "missing"), Path.Combine(_root, "out"), AllowLiveSource: true, MaxAttempts: 1),
                context,
                cts.Token));

        Assert.Equal(WorkflowState.Cancelled, context.StateMachine.State);
    }

    internal sealed class TestAccountConfirmation(bool confirmed) : Core.Ports.IAccountConfirmation
    {
        public Task<Core.Models.AccountConfirmation> ConfirmAsync(Core.Models.AccountIdentityReport report, CancellationToken cancellationToken)
            => Task.FromResult(new Core.Models.AccountConfirmation(confirmed, report.AccountCandidate));
    }
}
