using System.Text.Json;
using System.Text.Json.Serialization;
using WeChatVoice.Core.Errors;
using WeChatVoice.Core.Models;
using WeChatVoice.Core.Ports;
using WeChatVoice.Infrastructure.Sqlite;

namespace WeChatVoice.Workflows.Workflows;

/// <summary>
/// Executes materialization through the one-shot elevated Key Broker. The
/// Broker verifies the snapshot again, acquires and validates keys against
/// every database group, and writes a verified workspace; this executor then
/// maps elevation-declined (<see cref="UnauthorizedAccessException"/>) to the
/// typed <see cref="ErrorCode.UacElevationRejected"/> so hosts can prompt.
/// </summary>
public sealed class BrokerMaterializationExecutor(IBrokerClient brokerClient) : IMaterializationExecutor
{
    public string Id => "weixin-windows-4";

    public async Task<ExecutedMaterialization> ExecuteAsync(
        VerifiedRawSnapshot snapshot,
        string snapshotManifestPath,
        string outputRoot,
        string localWorkspacePath,
        string? confirmedAccountId,
        CancellationToken cancellationToken,
        IProgress<OperationProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        BrokerResponse response;
        try
        {
            response = await brokerClient.AcquireAndMaterializeAsync(
                snapshot,
                snapshotManifestPath,
                outputRoot,
                localWorkspacePath,
                cancellationToken,
                progress).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new AppFailureException(ErrorCode.UacElevationRejected, "Elevation for the Key Broker was declined.", exception);
        }

        if (!string.Equals(response.Status, "completed", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(response.ProfileId)
            || string.IsNullOrWhiteSpace(response.MaterializationId))
        {
            throw new BrokerTransportException(
                BrokerTransportErrorCode.Unknown,
                "The Key Broker did not complete materialization.",
                response.RequestId);
        }

        return new ExecutedMaterialization(localWorkspacePath, response.ProfileId, response.MaterializationId);
    }
}

/// <summary>
/// Development-only external decryptor executor. It hash-pins the external
/// backend, never accepts a key file, and creates the local workspace from the
/// validated materialization. It is only reachable through an explicit
/// allow-untrusted-backend opt-in.
/// </summary>
public sealed class ExternalMaterializationExecutor(
    IDatabaseMaterializationBackend backend,
    LocalWorkspaceCreator workspaceCreator) : IMaterializationExecutor
{
    public string Id => "external-decryptor";

    public async Task<ExecutedMaterialization> ExecuteAsync(
        VerifiedRawSnapshot snapshot,
        string snapshotManifestPath,
        string outputRoot,
        string localWorkspacePath,
        string? confirmedAccountId,
        CancellationToken cancellationToken,
        IProgress<OperationProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        progress?.Report(new OperationProgress(
            OperationPhase.Materialization,
            OperationStatus.Running,
            new OperationStage(OperationStageIds.Materializing, "正在执行外部解密器")));
        var materialization = await backend.MaterializeAsync(
            snapshot,
            new MaterializationOptions(Path.GetFullPath(outputRoot)),
            cancellationToken).ConfigureAwait(false);
        var localWorkspace = await workspaceCreator.CreateAsync(
            materialization,
            confirmedAccountId,
            SnapshotSourceIdentity.TryDerive(snapshot.Snapshot.Manifest.SourceDirectory, snapshot.Snapshot.Manifest.Files),
            cancellationToken).ConfigureAwait(false);
        var fullWorkspacePath = Path.GetFullPath(localWorkspacePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullWorkspacePath)!);
        await using var stream = new FileStream(fullWorkspacePath, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await JsonSerializer.SerializeAsync(stream, localWorkspace, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        }, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        return new ExecutedMaterialization(fullWorkspacePath, null, materialization.Result.WorkspaceId);
    }
}
