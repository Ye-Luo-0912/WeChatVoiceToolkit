using WeChatVoice.Core.Errors;
using WeChatVoice.Core.Models;
using WeChatVoice.Core.Ports;
using WeChatVoice.Workflows.Workflows;

namespace WeChatVoice.Workflows.Tests;

public sealed class MaterializationWorkflowTests
{
    private const string BackendId = "weixin-windows-4";

    private static MaterializationWorkflowRequest Request(string? externalDecryptor = null, bool allowUntrusted = false, string? account = null, string backend = BackendId)
        => new MaterializationWorkflowRequest(
            "C:\\snapshots\\s",
            SnapshotManifestPath: "C:\\snapshots\\s\\.wechatvoice\\snapshot-manifest.json",
            backend,
            externalDecryptor,
            AllowUntrustedBackend: allowUntrusted,
            AllowDevelopmentBroker: false,
            RequestedAccountId: account,
            "C:\\output",
            null);

    [Fact]
    public void SelectExecutor_uses_the_broker_by_default()
    {
        var broker = new FakeExecutor("broker");
        var workflow = new MaterializationWorkflow(broker);

        var selected = workflow.SelectExecutor(Request());
        Assert.Equal("broker", selected.Id);
    }

    [Fact]
    public void SelectExecutor_rejects_external_decryptor_without_explicit_opt_in()
    {
        var workflow = new MaterializationWorkflow(new FakeExecutor("broker"));
        var exception = Assert.Throws<ArgumentException>(() => workflow.SelectExecutor(Request(externalDecryptor: "C:\\backend.exe")));
        Assert.Contains("requires --allow-untrusted-backend", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectExecutor_accepts_external_decryptor_with_opt_in()
    {
        var workflow = new MaterializationWorkflow(
            new FakeExecutor("broker"),
            externalExecutorFactory: path => new FakeExecutor("external:" + path));
        var selected = workflow.SelectExecutor(Request(externalDecryptor: "C:\\backend.exe", allowUntrusted: true));
        Assert.Equal("external:C:\\backend.exe", selected.Id);
    }

    [Fact]
    public void SelectExecutor_rejects_unknown_backend()
    {
        var workflow = new MaterializationWorkflow(new FakeExecutor("broker"));
        Assert.Throws<Infrastructure.Materialization.MaterializationBackendUnavailableException>(() =>
            workflow.SelectExecutor(Request(backend: "unknown-backend")));
    }

    [Fact]
    public async Task ConfirmAccount_matches_explicit_account_without_user_prompt()
    {
        var confirmation = new TestAccountConfirmation();
        var context = new WorkflowContext(confirmation);
        var confirmed = await MaterializationWorkflow.ConfirmAccountAsync("wxid_owner", "wxid_owner", context, CancellationToken.None);

        Assert.Equal("wxid_owner", confirmed);
        Assert.Equal(0, confirmation.RequestCount);
    }

    [Fact]
    public async Task ConfirmAccount_rejects_mismatched_explicit_account()
    {
        var context = new WorkflowContext(new TestAccountConfirmation());
        var exception = await Assert.ThrowsAsync<AppFailureException>(() =>
            MaterializationWorkflow.ConfirmAccountAsync("wxid_owner", "wxid_other", context, CancellationToken.None));

        Assert.Equal(ErrorCode.AccountConfirmationRequired, exception.Code);
    }

    [Fact]
    public async Task ConfirmAccount_prompts_through_the_port_and_enters_awaiting_user()
    {
        var confirmation = new BlockingConfirmation();
        var context = new WorkflowContext(confirmation);
        Assert.True(context.StateMachine.TryStart());
        var task = MaterializationWorkflow.ConfirmAccountAsync("wxid_owner", null, context, CancellationToken.None);

        // The workflow blocks on the port; the state machine signals the host.
        Assert.Equal(WorkflowState.AwaitingUser, context.StateMachine.State);
        Assert.Equal(1, confirmation.RequestCount);

        confirmation.Complete(true, "wxid_owner");
        var confirmed = await task;
        Assert.Equal("wxid_owner", confirmed);
        Assert.Equal(WorkflowState.Running, context.StateMachine.State);
    }

    [Fact]
    public async Task ConfirmAccount_decline_fails_with_account_confirmation_required()
    {
        var confirmation = new TestAccountConfirmation(confirmed: false);
        var context = new WorkflowContext(confirmation);
        var exception = await Assert.ThrowsAsync<AppFailureException>(() =>
            MaterializationWorkflow.ConfirmAccountAsync("wxid_owner", null, context, CancellationToken.None));

        Assert.Equal(ErrorCode.AccountConfirmationRequired, exception.Code);
    }

    [Fact]
    public async Task BrokerExecutor_maps_uac_decline_to_typed_error()
    {
        var brokerClient = new ThrowingBrokerClient(new UnauthorizedAccessException("declined"));
        var executor = new BrokerMaterializationExecutor(brokerClient);

        var exception = await Assert.ThrowsAsync<AppFailureException>(() =>
            executor.ExecuteAsync(
                Verified(),
                "C:\\m.json",
                "C:\\out",
                "C:\\out\\workspace.json",
                "wxid_owner",
                CancellationToken.None));

        Assert.Equal(ErrorCode.UacElevationRejected, exception.Code);
    }

    [Fact]
    public async Task BrokerExecutor_translates_transport_failure_to_typed_exception()
    {
        var brokerClient = new ThrowingBrokerClient(new BrokerTransportException(BrokerTransportErrorCode.MalformedRequest, "bad"));
        var executor = new BrokerMaterializationExecutor(brokerClient);

        await Assert.ThrowsAsync<BrokerTransportException>(() =>
            executor.ExecuteAsync(
                Verified(),
                "C:\\m.json",
                "C:\\out",
                "C:\\out\\workspace.json",
                "wxid_owner",
                CancellationToken.None));
    }

    private static VerifiedRawSnapshot Verified()
        => new VerifiedRawSnapshot(
            new RawSnapshot(new SnapshotManifest("C:\\root", "C:\\root", DateTimeOffset.UtcNow, Files: []), "C:\\root"),
            DateTimeOffset.UtcNow);


    private sealed class FakeExecutor(string id) : IMaterializationExecutor
    {
        public string Id => id;

        public Task<ExecutedMaterialization> ExecuteAsync(
            VerifiedRawSnapshot snapshot,
            string snapshotManifestPath,
            string outputRoot,
            string localWorkspacePath,
            string? confirmedAccountId,
            CancellationToken cancellationToken,
            IProgress<OperationProgress>? progress = null)
            => throw new NotSupportedException("Not reached in routing tests.");
    }

    private sealed class TestAccountConfirmation(bool confirmed = true) : IAccountConfirmation
    {
        public int RequestCount { get; private set; }

        public Task<AccountConfirmation> ConfirmAsync(AccountIdentityReport report, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new AccountConfirmation(confirmed, report.AccountCandidate));
        }
    }

    /// <summary>Confirmation port that blocks until the host answers.</summary>
    private sealed class BlockingConfirmation : IAccountConfirmation
    {
        private readonly TaskCompletionSource<AccountConfirmation> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int RequestCount { get; private set; }

        public Task<AccountConfirmation> ConfirmAsync(AccountIdentityReport report, CancellationToken cancellationToken)
        {
            RequestCount++;
            return _tcs.Task.WaitAsync(cancellationToken);
        }

        public void Complete(bool confirmed, string? accountId)
            => _tcs.TrySetResult(new AccountConfirmation(confirmed, accountId));
    }

    private sealed class ThrowingBrokerClient(Exception exception) : IBrokerClient
    {
        public Task<BrokerResponse> AcquireAndMaterializeAsync(
            VerifiedRawSnapshot snapshot,
            string snapshotManifestPath,
            string outputRoot,
            string workspaceOutput,
            CancellationToken cancellationToken,
            IProgress<OperationProgress>? progress = null)
            => Task.FromException<BrokerResponse>(exception);
    }
}
