using System.Text.Json;
using WeChatVoice.Core.Models;
using WeChatVoice.Infrastructure.Materialization;
using WeChatVoice.Infrastructure.Serialization;
using WeChatVoice.Infrastructure.Sqlite;
using WeChatVoice.Workflows.Workflows;

namespace WeChatVoice.Tests;

public sealed class MaterializationRecoveryTests
{
    [Fact]
    public async Task Materialization_state_is_monotonic_and_completed_is_terminal()
    {
        using var temporary = new TestTemporaryDirectory();
        var outputRoot = temporary.CreateDirectory("state-machine");
        var operationId = "state-test";
        await WriteManifestAsync(outputRoot, new MaterializationManifest(
            "state-workspace",
            "state-snapshot",
            "state-backend",
            "1",
            new string('a', 64),
            [],
            []));
        var binding = await MaterializationStateStore.ReadManifestBindingAsync(outputRoot, CancellationToken.None);

        await MaterializationStateStore.CreateStagingStateAsync(
            outputRoot,
            operationId,
            new MaterializationStateBinding(binding.SourceSnapshotId, binding.BackendId),
            CancellationToken.None);
        await MaterializationStateStore.TransitionAsync(
            outputRoot,
            [MaterializationCommitStates.Staging],
            MaterializationCommitStates.DatabasesCommitted,
            operationId,
            null,
            CancellationToken.None,
            binding);
        await MaterializationStateStore.TransitionAsync(
            outputRoot,
            [MaterializationCommitStates.DatabasesCommitted],
            MaterializationCommitStates.WorkspaceCommitted,
            operationId,
            null,
            CancellationToken.None,
            binding);
        await MaterializationStateStore.TransitionAsync(
            outputRoot,
            [MaterializationCommitStates.WorkspaceCommitted],
            MaterializationCommitStates.Completed,
            operationId,
            null,
            CancellationToken.None,
            binding);

        Assert.False(await MaterializationStateStore.TryTransitionToFailedRecoverableAsync(
            outputRoot,
            operationId,
            "late-response-failure",
            CancellationToken.None));

        var state = await MaterializationStateStore.ReadAsync(outputRoot, CancellationToken.None);
        Assert.Equal(MaterializationCommitStates.Completed, state.State);
        Assert.Equal(operationId, state.OperationId);
    }

    [Fact]
    public async Task Missing_state_can_only_be_created_as_bound_staging_and_binding_is_immutable()
    {
        using var temporary = new TestTemporaryDirectory();
        var outputRoot = temporary.CreateDirectory("state-binding");

        await Assert.ThrowsAsync<MaterializationStateTransitionException>(() =>
            MaterializationStateStore.TransitionAsync(
                outputRoot,
                [],
                MaterializationCommitStates.Completed,
                "invalid-create",
                null,
                CancellationToken.None));

        await WriteManifestAsync(outputRoot, new MaterializationManifest(
            "bound-workspace",
            "bound-snapshot",
            "bound-backend",
            "1",
            new string('d', 64),
            [],
            []));
        var binding = await MaterializationStateStore.ReadManifestBindingAsync(outputRoot, CancellationToken.None);
        await MaterializationStateStore.CreateStagingStateAsync(
            outputRoot,
            "bound-operation",
            new MaterializationStateBinding(binding.SourceSnapshotId, binding.BackendId),
            CancellationToken.None);

        await Assert.ThrowsAsync<MaterializationStateTransitionException>(() =>
            MaterializationStateStore.TransitionAsync(
                outputRoot,
                [MaterializationCommitStates.Staging],
                MaterializationCommitStates.DatabasesCommitted,
                "wrong-binding",
                null,
                CancellationToken.None,
                new MaterializationStateBinding(
                    "different-snapshot",
                    binding.BackendId,
                    binding.ManifestSha256,
                    binding.WorkspaceId)));
    }

    [Fact]
    public async Task Materialization_lock_can_coexist_with_root_cleanup()
    {
        using var temporary = new TestTemporaryDirectory();
        var outputRoot = temporary.CreateDirectory("locked-delete");
        await using var stateLock = await MaterializationStateStore.AcquireLockAsync(outputRoot, CancellationToken.None);

        Directory.Delete(outputRoot, recursive: true);

        Assert.False(Directory.Exists(outputRoot));
    }

    [Fact]
    public async Task RecoverAsync_adopts_databases_committed_without_redecrypting()
    {
        using var temporary = new TestTemporaryDirectory();
        var outputRoot = temporary.CreateDirectory("materialized");
        var databasePath = Path.Combine(outputRoot, "databases", "message_0.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await SqliteSchemaInspectorTests.CreateSampleDatabaseAsync(databasePath);
        var info = new FileInfo(databasePath);
        var hash = await FileHashing.ComputeSha256Async(databasePath, CancellationToken.None);
        var schema = await new SqliteSchemaInspector().InspectAsync(databasePath, CancellationToken.None);
        var backendOutputPath = Path.Combine(outputRoot, ".wechatvoice", "materialization-output.json");
        Directory.CreateDirectory(Path.GetDirectoryName(backendOutputPath)!);
        await File.WriteAllTextAsync(backendOutputPath, "{\"formatVersion\":1,\"databases\":[]}");
        var backendOutputInfo = new FileInfo(backendOutputPath);
        var backendOutputHash = await FileHashing.ComputeSha256Async(backendOutputPath, CancellationToken.None);
        await WriteManifestAsync(outputRoot, new MaterializationManifest(
            "materialization-recover-1",
            "snapshot-1",
            "test-backend",
            "1",
            new string('b', 64),
            [new MaterializedDatabase("message_0.db", "group-1", "databases/message_0.db", "message", 0, hash, info.Length, schema.SchemaFingerprint!)],
            [
                new MaterializationFile("databases/message_0.db", hash, info.Length),
                new MaterializationFile(".wechatvoice/materialization-output.json", backendOutputHash, backendOutputInfo.Length),
            ]));
        await CreateBoundDatabaseCommittedStateAsync(outputRoot);

        var workspacePath = temporary.GetPath("recovered.workspace.json");
        var verified = await new MaterializationRecoveryService().RecoverAsync(outputRoot, workspacePath, null, CancellationToken.None);

        Assert.Equal(Path.GetFullPath(outputRoot), verified.Workspace.SourceRoot);
        Assert.True(File.Exists(workspacePath));
        var state = await MaterializationStateStore.ReadAsync(outputRoot, CancellationToken.None);
        Assert.Equal(MaterializationCommitStates.Completed, state.State);
    }

    [Fact]
    public async Task RecoverAsync_marks_a_changed_bundle_failed_recoverable()
    {
        using var temporary = new TestTemporaryDirectory();
        var outputRoot = temporary.CreateDirectory("materialized");
        var databasePath = Path.Combine(outputRoot, "databases", "message_0.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await SqliteSchemaInspectorTests.CreateSampleDatabaseAsync(databasePath);
        var info = new FileInfo(databasePath);
        var hash = await FileHashing.ComputeSha256Async(databasePath, CancellationToken.None);
        var schema = await new SqliteSchemaInspector().InspectAsync(databasePath, CancellationToken.None);
        await WriteManifestAsync(outputRoot, new MaterializationManifest(
            "materialization-recover-2",
            "snapshot-2",
            "test-backend",
            "1",
            new string('c', 64),
            [new MaterializedDatabase("message_0.db", "group-2", "databases/message_0.db", "message", 0, hash, info.Length, schema.SchemaFingerprint!)],
            [new MaterializationFile("databases/message_0.db", hash, info.Length)]));
        await CreateBoundDatabaseCommittedStateAsync(outputRoot);
        await File.AppendAllBytesAsync(databasePath, [0x7F]);

        await Assert.ThrowsAsync<InvalidDataException>(() => new MaterializationRecoveryService().RecoverAsync(
            outputRoot,
            temporary.GetPath("failed.workspace.json"),
            null,
            CancellationToken.None));

        var state = await MaterializationStateStore.ReadAsync(outputRoot, CancellationToken.None);
        Assert.Equal(MaterializationCommitStates.FailedRecoverable, state.State);
    }

    [Fact]
    public async Task RecoverAsync_requires_confirmation_for_a_path_derived_account()
    {
        using var temporary = new TestTemporaryDirectory();
        var fixture = await CreateCommittedWorkspaceAsync(
            temporary,
            "account-confirmation",
            accountId: "wxid_owner",
            recover: false);

        await Assert.ThrowsAsync<InvalidDataException>(() => new MaterializationRecoveryService().RecoverAsync(
            fixture.OutputRoot,
            fixture.WorkspacePath,
            null,
            CancellationToken.None));

        var failedState = await MaterializationStateStore.ReadAsync(fixture.OutputRoot, CancellationToken.None);
        Assert.Equal(MaterializationCommitStates.FailedRecoverable, failedState.State);

        var identity = new AccountIdentity(
            AccountIdentityState.Candidate,
            null,
            UserConfirmationState.Confirmed,
            "wxid_owner");
        var recovered = await new MaterializationRecoveryService().RecoverAsync(
            fixture.OutputRoot,
            fixture.WorkspacePath,
            "wxid_owner",
            CancellationToken.None,
            identity);

        Assert.Equal(UserConfirmationState.Confirmed, recovered.Workspace.AccountIdentity.UserConfirmation);
        Assert.Equal("wxid_owner", recovered.Workspace.AccountIdentity.ConfirmedAccountId);
        var completedState = await MaterializationStateStore.ReadAsync(fixture.OutputRoot, CancellationToken.None);
        Assert.Equal(MaterializationCommitStates.Completed, completedState.State);
    }

    [Fact]
    public async Task RepairWorkspaceAsync_recreates_missing_document_without_downgrading_completed_state()
    {
        using var temporary = new TestTemporaryDirectory();
        var fixture = await CreateCommittedWorkspaceAsync(
            temporary,
            "repair-completed",
            accountId: "wxid_owner",
            recover: false);

        var identity = new AccountIdentity(AccountIdentityState.Candidate, null, UserConfirmationState.Confirmed, "wxid_owner");
        await new MaterializationRecoveryService().RecoverAsync(
            fixture.OutputRoot,
            fixture.WorkspacePath,
            "wxid_owner",
            CancellationToken.None,
            identity);

        File.Delete(fixture.WorkspacePath);
        var repaired = await new MaterializationRecoveryService().RepairWorkspaceAsync(
            fixture.OutputRoot,
            fixture.WorkspacePath,
            "wxid_owner",
            CancellationToken.None,
            identity);

        Assert.True(File.Exists(fixture.WorkspacePath));
        Assert.Equal("wxid_owner", repaired.Workspace.AccountIdentity.ConfirmedAccountId);
        var state = await MaterializationStateStore.ReadAsync(fixture.OutputRoot, CancellationToken.None);
        Assert.Equal(MaterializationCommitStates.Completed, state.State);
    }

    [Fact]
    public async Task Delete_materialized_workspace_revalidates_and_preserves_external_data()
    {
        using var temporary = new TestTemporaryDirectory();
        var fixture = await CreateCommittedWorkspaceAsync(temporary, "delete-success");
        var snapshotSentinel = temporary.GetPath("snapshot-sentinel.txt");
        var exportSentinel = temporary.GetPath("export-sentinel.silk");
        await File.WriteAllTextAsync(snapshotSentinel, "snapshot");
        await File.WriteAllTextAsync(exportSentinel, "silk");

        var context = new WorkflowContext(new TestConfirmation());
        var result = await new DeleteMaterializedWorkspaceWorkflow().RunAsync(
            fixture.WorkspacePath,
            context,
            CancellationToken.None);

        Assert.Equal(fixture.WorkspaceId, result.WorkspaceId);
        Assert.Equal(1, result.DatabaseCount);
        Assert.False(Directory.Exists(fixture.OutputRoot));
        Assert.True(result.WorkspaceDocumentDeleted);
        Assert.False(File.Exists(fixture.WorkspacePath));
        Assert.True(File.Exists(snapshotSentinel));
        Assert.True(File.Exists(exportSentinel));
        Assert.Equal(WorkflowState.Completed, context.StateMachine.State);
    }

    [Fact]
    public async Task Delete_preview_revalidates_without_deleting_the_workspace()
    {
        using var temporary = new TestTemporaryDirectory();
        var fixture = await CreateCommittedWorkspaceAsync(temporary, "delete-preview");

        var context = new WorkflowContext(new TestConfirmation());
        var preview = await new DeleteMaterializedWorkspaceWorkflow().PreviewAsync(
            fixture.WorkspacePath,
            context,
            CancellationToken.None);

        Assert.Equal(fixture.WorkspaceId, preview.WorkspaceId);
        Assert.Equal(1, preview.DatabaseCount);
        Assert.True(preview.TotalBytes > 0);
        Assert.True(Directory.Exists(fixture.OutputRoot));
        Assert.Equal(WorkflowState.Completed, context.StateMachine.State);
    }

    [Fact]
    public async Task Delete_materialized_workspace_rejects_an_unmanifested_file()
    {
        using var temporary = new TestTemporaryDirectory();
        var fixture = await CreateCommittedWorkspaceAsync(temporary, "delete-extra");
        await File.WriteAllTextAsync(Path.Combine(fixture.OutputRoot, "unexpected.db"), "not covered");

        var context = new WorkflowContext(new TestConfirmation());
        await Assert.ThrowsAsync<InvalidDataException>(() => new DeleteMaterializedWorkspaceWorkflow().RunAsync(
            fixture.WorkspacePath,
            context,
            CancellationToken.None));

        Assert.True(Directory.Exists(fixture.OutputRoot));
        Assert.Equal(WorkflowState.Failed, context.StateMachine.State);
    }

    private static async Task<MaterializedWorkspaceFixture> CreateCommittedWorkspaceAsync(
        TestTemporaryDirectory temporary,
        string name,
        string? accountId = null,
        bool recover = true)
    {
        var outputRoot = temporary.CreateDirectory(Path.Combine(name, "materialized"));
        var databasePath = Path.Combine(outputRoot, "databases", "message_0.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await SqliteSchemaInspectorTests.CreateSampleDatabaseAsync(databasePath);
        var info = new FileInfo(databasePath);
        var hash = await FileHashing.ComputeSha256Async(databasePath, CancellationToken.None);
        var schema = await new SqliteSchemaInspector().InspectAsync(databasePath, CancellationToken.None);
        var backendOutputPath = Path.Combine(outputRoot, ".wechatvoice", "materialization-output.json");
        Directory.CreateDirectory(Path.GetDirectoryName(backendOutputPath)!);
        await File.WriteAllTextAsync(backendOutputPath, "{\"formatVersion\":1,\"databases\":[]}");
        var backendOutputInfo = new FileInfo(backendOutputPath);
        var backendOutputHash = await FileHashing.ComputeSha256Async(backendOutputPath, CancellationToken.None);
        await WriteManifestAsync(outputRoot, new MaterializationManifest(
            "materialization-" + name,
            "snapshot-" + name,
            "test-backend",
            "1",
            new string('b', 64),
            [new MaterializedDatabase("message_0.db", "group-1", "databases/message_0.db", "message", 0, hash, info.Length, schema.SchemaFingerprint!)],
            [
                new MaterializationFile("databases/message_0.db", hash, info.Length),
                new MaterializationFile(".wechatvoice/materialization-output.json", backendOutputHash, backendOutputInfo.Length),
            ],
            AccountId: accountId));
        await CreateBoundDatabaseCommittedStateAsync(outputRoot);

        var workspacePath = temporary.GetPath(Path.Combine(name, "workspace.json"));
        if (recover)
        {
            var verified = await new MaterializationRecoveryService().RecoverAsync(outputRoot, workspacePath, null, CancellationToken.None);
            var state = await MaterializationStateStore.ReadAsync(outputRoot, CancellationToken.None);
            Assert.Equal(MaterializationCommitStates.Completed, state.State);
            return new MaterializedWorkspaceFixture(outputRoot, workspacePath, verified.Workspace.WorkspaceId);
        }

        return new MaterializedWorkspaceFixture(outputRoot, workspacePath, "materialization-" + name);
    }

    private sealed record MaterializedWorkspaceFixture(string OutputRoot, string WorkspacePath, string WorkspaceId);

    private sealed class TestConfirmation : WeChatVoice.Core.Ports.IAccountConfirmation
    {
        public Task<AccountConfirmation> ConfirmAsync(
            AccountIdentityReport report,
            CancellationToken cancellationToken)
            => Task.FromResult(new AccountConfirmation(true, report.AccountCandidate));
    }

    private static async Task WriteManifestAsync(string outputRoot, MaterializationManifest manifest)
    {
        var metadata = Path.Combine(outputRoot, ".wechatvoice");
        Directory.CreateDirectory(metadata);
        await using var stream = File.Create(Path.Combine(metadata, "materialization-manifest.json"));
        await JsonSerializer.SerializeAsync(stream, manifest, InfrastructureJson.Indented);
    }

    private static async Task CreateBoundDatabaseCommittedStateAsync(string outputRoot)
    {
        var binding = await MaterializationStateStore.ReadManifestBindingAsync(outputRoot, CancellationToken.None);
        await MaterializationStateStore.CreateStagingStateAsync(
            outputRoot,
            "test-operation-" + Guid.NewGuid().ToString("N"),
            new MaterializationStateBinding(binding.SourceSnapshotId, binding.BackendId),
            CancellationToken.None);
        await MaterializationStateStore.TransitionAsync(
            outputRoot,
            [MaterializationCommitStates.Staging],
            MaterializationCommitStates.DatabasesCommitted,
            "test-operation",
            null,
            CancellationToken.None,
            binding);
    }
}
