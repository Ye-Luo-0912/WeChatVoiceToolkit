using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using WeChatVoice.Core.Errors;
using WeChatVoice.Core.Models;
using WeChatVoice.Core.Ports;
using WeChatVoice.Infrastructure.Materialization;
using WeChatVoice.Infrastructure.Serialization;
using WeChatVoice.Infrastructure.Sqlite;
using WeChatVoice.Workflows.Workflows;

namespace WeChatVoice.Workflows.Tests;

/// <summary>
/// Covers the shared "continue existing project" workflow: how local state is
/// classified and how a verified/recoverable/repairable project is resumed
/// without re-running Snapshot, UAC, or materialization.
/// </summary>
public sealed class ProjectStateWorkflowTests
{
    private static readonly JsonSerializerOptions ManifestJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    [Fact]
    public async Task Inspect_returns_missing_when_no_local_state_exists()
    {
        using var temp = new TempDirectory();
        var workspacePath = temp.Combine("state", "fingerprint.workspace.json");

        var status = await new ProjectStateWorkflow()
            .InspectAsync(new ProjectStateInspectRequest(workspacePath), Context(), CancellationToken.None);

        Assert.Equal(ProjectStageState.Missing, status.State);
        Assert.False(status.RequiresElevation);
        Assert.True(status.ProducesNewDiskData);
    }

    [Fact]
    public async Task Inspect_returns_busy_when_materialization_is_staging()
    {
        using var temp = new TempDirectory();
        var fixture = await CreateMaterializedRootAsync(temp, "busy", state: MaterializationCommitStates.Staging);
        var workspacePath = Path.Combine(temp.Root, "busy", "materialized.workspace.json");

        var status = await new ProjectStateWorkflow()
            .InspectAsync(new ProjectStateInspectRequest(workspacePath), Context(), CancellationToken.None);

        Assert.Equal(ProjectStageState.Busy, status.State);
        Assert.False(status.ProducesNewDiskData);
    }

    [Fact]
    public async Task Inspect_returns_invalid_when_workspace_document_is_corrupt_and_no_root_exists()
    {
        using var temp = new TempDirectory();
        var workspacePath = temp.Combine("broken", "fingerprint.workspace.json");
        Directory.CreateDirectory(Path.GetDirectoryName(workspacePath)!);
        await File.WriteAllTextAsync(workspacePath, "{ not valid workspace json");

        var status = await new ProjectStateWorkflow()
            .InspectAsync(new ProjectStateInspectRequest(workspacePath), Context(), CancellationToken.None);

        Assert.Equal(ProjectStageState.Invalid, status.State);
    }

    [Fact]
    public async Task Inspect_returns_valid_reusable_when_workspace_verifies()
    {
        using var temp = new TempDirectory();
        var fixture = await CreateRecoveredWorkspaceAsync(temp, "reuse");

        var status = await new ProjectStateWorkflow()
            .InspectAsync(new ProjectStateInspectRequest(fixture.WorkspacePath), Context(), CancellationToken.None);

        Assert.Equal(ProjectStageState.ValidReusable, status.State);
        Assert.NotNull(status.VerifiedWorkspace);
        Assert.False(status.RequiresElevation);
        Assert.False(status.ProducesNewDiskData);
    }

    [Fact]
    public async Task Inspect_returns_stale_when_workspace_belongs_to_another_account()
    {
        using var temp = new TempDirectory();
        var fixture = await CreateRecoveredWorkspaceAsync(
            temp,
            "account",
            accountId: "wxid_owner",
            accountIdentity: new AccountIdentity(
                AccountIdentityState.Confirmed,
                null,
                UserConfirmationState.Confirmed,
                "wxid_owner"));

        var status = await new ProjectStateWorkflow()
            .InspectAsync(new ProjectStateInspectRequest(fixture.WorkspacePath, ExpectedAccountId: "wxid_other"), Context(), CancellationToken.None);

        Assert.Equal(ProjectStageState.Stale, status.State);
    }

    [Fact]
    public async Task Inspect_returns_invalid_repairable_when_document_is_corrupt_but_materialization_completed()
    {
        using var temp = new TempDirectory();
        var fixture = await CreateRecoveredWorkspaceAsync(temp, "repairable");
        await File.WriteAllTextAsync(fixture.WorkspacePath, "{ corrupt");

        var status = await new ProjectStateWorkflow()
            .InspectAsync(new ProjectStateInspectRequest(fixture.WorkspacePath), Context(), CancellationToken.None);

        Assert.Equal(ProjectStageState.Invalid, status.State);
        Assert.True(status.ProducesNewDiskData);
    }

    [Fact]
    public async Task Inspect_returns_recoverable_when_databases_committed_but_workspace_not()
    {
        using var temp = new TempDirectory();
        var fixture = await CreateMaterializedRootAsync(temp, "recoverable", state: MaterializationCommitStates.DatabasesCommitted);
        var workspacePath = Path.Combine(temp.Root, "recoverable", "materialized.workspace.json");

        var status = await new ProjectStateWorkflow()
            .InspectAsync(new ProjectStateInspectRequest(workspacePath), Context(), CancellationToken.None);

        Assert.Equal(ProjectStageState.Recoverable, status.State);
        Assert.True(status.ProducesNewDiskData);
    }

    [Fact]
    public async Task Resume_reuses_a_valid_workspace_without_producing_disk_data_or_elevation()
    {
        using var temp = new TempDirectory();
        var fixture = await CreateRecoveredWorkspaceAsync(temp, "resume-reuse");

        var result = await new ProjectStateWorkflow()
            .ResumeAsync(new ProjectStateResumeRequest(fixture.WorkspacePath), Context(), CancellationToken.None);

        Assert.Equal(ProjectStageState.ValidReusable, result.State);
        Assert.False(result.RequiresElevation);
        Assert.False(result.ProducedNewDiskData);
        Assert.Equal(fixture.WorkspaceId, result.Workspace.Workspace.WorkspaceId);
    }

    [Fact]
    public async Task Resume_recovers_a_databases_committed_state_through_the_workspace_workflow()
    {
        using var temp = new TempDirectory();
        var fixture = await CreateMaterializedRootAsync(temp, "resume-recover", state: MaterializationCommitStates.DatabasesCommitted);
        var workspacePath = Path.Combine(temp.Root, "resume-recover", "materialized.workspace.json");
        var canned = TestFixtures.Verified(TestFixtures.MakeWorkspace());
        var fakeWorkspace = new FakeWorkspaceWorkflow(canned);
        var workflow = new ProjectStateWorkflow(workspace: fakeWorkspace);

        var result = await workflow.ResumeAsync(
            new ProjectStateResumeRequest(workspacePath),
            Context(),
            CancellationToken.None);

        Assert.Equal(ProjectStageState.ValidReusable, result.State);
        Assert.True(result.ProducedNewDiskData);
        Assert.Equal(1, fakeWorkspace.RecoverCalls);
    }

    [Fact]
    public async Task Resume_repairs_a_corrupt_document_after_completed_materialization()
    {
        using var temp = new TempDirectory();
        var fixture = await CreateRecoveredWorkspaceAsync(temp, "resume-repair");
        await File.WriteAllTextAsync(fixture.WorkspacePath, "{ corrupt");
        var canned = TestFixtures.Verified(TestFixtures.MakeWorkspace());
        var fakeWorkspace = new FakeWorkspaceWorkflow(canned);
        var workflow = new ProjectStateWorkflow(workspace: fakeWorkspace);

        var result = await workflow.ResumeAsync(
            new ProjectStateResumeRequest(fixture.WorkspacePath),
            Context(),
            CancellationToken.None);

        Assert.Equal(ProjectStageState.ValidReusable, result.State);
        Assert.True(result.ProducedNewDiskData);
        Assert.Equal(1, fakeWorkspace.RepairCalls);
    }

    [Fact]
    public async Task Resume_throws_a_typed_error_when_no_local_state_exists()
    {
        using var temp = new TempDirectory();
        var workspacePath = temp.Combine("missing", "fingerprint.workspace.json");

        var exception = await Assert.ThrowsAsync<AppFailureException>(() =>
            new ProjectStateWorkflow().ResumeAsync(
                new ProjectStateResumeRequest(workspacePath),
                Context(),
                CancellationToken.None));

        Assert.Equal(ErrorCode.InvalidRequest, exception.Code);
    }

    private static WorkflowContext Context() => new(new TestConfirmation());

    // ---- Fixtures ----

    private static async Task<MaterializedFixture> CreateRecoveredWorkspaceAsync(
        TempDirectory temp,
        string name,
        string? accountId = null,
        AccountIdentity? accountIdentity = null)
    {
        var fixture = await CreateMaterializedRootAsync(temp, name, state: MaterializationCommitStates.DatabasesCommitted, accountId: accountId);
        var workspacePath = Path.Combine(temp.Root, name, "materialized.workspace.json");
        var verified = await new MaterializationRecoveryService().RecoverAsync(
            fixture.OutputRoot,
            workspacePath,
            null,
            CancellationToken.None,
            accountIdentity);
        var state = await MaterializationStateStore.ReadAsync(fixture.OutputRoot, CancellationToken.None);
        Assert.Equal(MaterializationCommitStates.Completed, state.State);
        return new MaterializedFixture(fixture.OutputRoot, workspacePath, verified.Workspace.WorkspaceId);
    }

    private static async Task<MaterializedFixture> CreateMaterializedRootAsync(
        TempDirectory temp,
        string name,
        string state,
        string? accountId = null)
    {
        var outputRoot = Path.Combine(temp.Root, name, "materialized");
        Directory.CreateDirectory(outputRoot);
        var databasePath = Path.Combine(outputRoot, "databases", "message_0.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await CreateSampleDatabaseAsync(databasePath);

        var info = new FileInfo(databasePath);
        var hash = await FileHashing.ComputeSha256Async(databasePath, CancellationToken.None);
        var schema = await new SqliteSchemaInspector().InspectAsync(databasePath, CancellationToken.None);

        var backendOutputPath = Path.Combine(outputRoot, ".wechatvoice", "materialization-output.json");
        Directory.CreateDirectory(Path.GetDirectoryName(backendOutputPath)!);
        await File.WriteAllTextAsync(backendOutputPath, "{\"formatVersion\":1,\"databases\":[]}");
        var backendInfo = new FileInfo(backendOutputPath);
        var backendHash = await FileHashing.ComputeSha256Async(backendOutputPath, CancellationToken.None);

        var manifest = new MaterializationManifest(
            "materialization-" + name,
            "snapshot-" + name,
            "test-backend",
            "1",
            new string('b', 64),
            [new MaterializedDatabase("message_0.db", "group-1", "databases/message_0.db", "message", 0, hash, info.Length, schema.SchemaFingerprint!)],
            [
                new MaterializationFile("databases/message_0.db", hash, info.Length),
                new MaterializationFile(".wechatvoice/materialization-output.json", backendHash, backendInfo.Length),
            ],
            AccountId: accountId);
        await WriteManifestAsync(outputRoot, manifest);
        var binding = await MaterializationStateStore.ReadManifestBindingAsync(outputRoot, CancellationToken.None);
        await MaterializationStateStore.CreateStagingStateAsync(
            outputRoot,
            "test-operation",
            new MaterializationStateBinding(binding.SourceSnapshotId, binding.BackendId),
            CancellationToken.None);

        if (state == MaterializationCommitStates.Staging)
        {
            return new MaterializedFixture(outputRoot, string.Empty, "materialization-" + name);
        }

        await MaterializationStateStore.TransitionAsync(
            outputRoot,
            [MaterializationCommitStates.Staging],
            MaterializationCommitStates.DatabasesCommitted,
            "test-operation",
            null,
            CancellationToken.None,
            binding);
        return new MaterializedFixture(outputRoot, string.Empty, "materialization-" + name);
    }

    private static async Task WriteManifestAsync(string outputRoot, MaterializationManifest manifest)
    {
        var metadata = Path.Combine(outputRoot, ".wechatvoice");
        Directory.CreateDirectory(metadata);
        await using var stream = File.Create(Path.Combine(metadata, "materialization-manifest.json"));
        await JsonSerializer.SerializeAsync(stream, manifest, ManifestJson);
    }

    private static async Task CreateSampleDatabaseAsync(string databasePath)
    {
        File.WriteAllBytes(databasePath, Array.Empty<byte>());
        try
        {
            await new SqliteSchemaInspector().InspectAsync(databasePath, CancellationToken.None);
        }
        catch (SqliteException)
        {
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE voice_records (
                id INTEGER PRIMARY KEY,
                payload BLOB NOT NULL,
                direction TEXT NOT NULL DEFAULT 'incoming'
            );
            """;
        await command.ExecuteNonQueryAsync();
    }

    private sealed record MaterializedFixture(string OutputRoot, string WorkspacePath, string WorkspaceId);

    private sealed class TestConfirmation : IAccountConfirmation
    {
        public Task<AccountConfirmation> ConfirmAsync(
            AccountIdentityReport report,
            CancellationToken cancellationToken)
            => Task.FromResult(new AccountConfirmation(true, report.AccountCandidate));
    }

    private sealed class FakeWorkspaceWorkflow(VerifiedLocalWorkspace result) : IWorkspaceWorkflow
    {
        public int RecoverCalls { get; private set; }
        public int RepairCalls { get; private set; }

        public Task<WorkspaceCreateResult> CreateAsync(WorkspaceCreateRequest request, WorkflowContext context, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<VerifiedLocalWorkspace> VerifyAsync(string workspacePath, WorkflowContext context, CancellationToken cancellationToken)
            => Task.FromResult(result);

        public Task<VerifiedLocalWorkspace> RecoverMaterializationAsync(MaterializationRecoveryRequest request, WorkflowContext context, CancellationToken cancellationToken)
        {
            RecoverCalls++;
            return Task.FromResult(result);
        }

        public Task<VerifiedLocalWorkspace> RepairMaterializationAsync(MaterializationRecoveryRequest request, WorkflowContext context, CancellationToken cancellationToken)
        {
            RepairCalls++;
            return Task.FromResult(result);
        }

        public Task<MaterializationRecoveryAssessment> AssessMaterializationRecoveryAsync(string outputDirectory, string? workspaceOutputPath, WorkflowContext context, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<WorkspaceDeletionPreview> PreviewDeleteMaterializedAsync(string workspacePath, WorkflowContext context, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<WorkspaceDeletionResult> DeleteMaterializedAsync(string workspacePath, WorkflowContext context, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Root = Path.Combine(Path.GetTempPath(), "WeChatVoiceToolkit.ProjectStateWorkflowTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string Combine(params string[] segments) => Path.Combine([Root, .. segments]);

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
