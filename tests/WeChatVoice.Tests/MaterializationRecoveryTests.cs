using System.Text.Json;
using WeChatVoice.Core.Models;
using WeChatVoice.Infrastructure.Materialization;
using WeChatVoice.Infrastructure.Serialization;
using WeChatVoice.Infrastructure.Sqlite;

namespace WeChatVoice.Tests;

public sealed class MaterializationRecoveryTests
{
    [Fact]
    public async Task Materialization_state_is_monotonic_and_completed_is_terminal()
    {
        using var temporary = new TestTemporaryDirectory();
        var outputRoot = temporary.CreateDirectory("state-machine");
        var operationId = "state-test";

        await MaterializationStateStore.TransitionAsync(
            outputRoot,
            Array.Empty<string>(),
            MaterializationCommitStates.Staging,
            operationId,
            null,
            CancellationToken.None);
        await MaterializationStateStore.TransitionAsync(
            outputRoot,
            [MaterializationCommitStates.Staging],
            MaterializationCommitStates.DatabasesCommitted,
            operationId,
            null,
            CancellationToken.None);
        await MaterializationStateStore.TransitionAsync(
            outputRoot,
            [MaterializationCommitStates.DatabasesCommitted],
            MaterializationCommitStates.WorkspaceCommitted,
            operationId,
            null,
            CancellationToken.None);
        await MaterializationStateStore.TransitionAsync(
            outputRoot,
            [MaterializationCommitStates.WorkspaceCommitted],
            MaterializationCommitStates.Completed,
            operationId,
            null,
            CancellationToken.None);

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
        await MaterializationStateStore.WriteAsync(outputRoot, MaterializationCommitStates.DatabasesCommitted, CancellationToken.None);

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
        await MaterializationStateStore.WriteAsync(outputRoot, MaterializationCommitStates.DatabasesCommitted, CancellationToken.None);
        await File.AppendAllBytesAsync(databasePath, [0x7F]);

        await Assert.ThrowsAsync<InvalidDataException>(() => new MaterializationRecoveryService().RecoverAsync(
            outputRoot,
            temporary.GetPath("failed.workspace.json"),
            null,
            CancellationToken.None));

        var state = await MaterializationStateStore.ReadAsync(outputRoot, CancellationToken.None);
        Assert.Equal(MaterializationCommitStates.FailedRecoverable, state.State);
    }

    private static async Task WriteManifestAsync(string outputRoot, MaterializationManifest manifest)
    {
        var metadata = Path.Combine(outputRoot, ".wechatvoice");
        Directory.CreateDirectory(metadata);
        await using var stream = File.Create(Path.Combine(metadata, "materialization-manifest.json"));
        await JsonSerializer.SerializeAsync(stream, manifest, InfrastructureJson.Indented);
    }
}
