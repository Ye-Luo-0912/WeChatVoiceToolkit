using WeChatVoice.Core.Errors;
using WeChatVoice.Core.Models;
using WeChatVoice.Core.Ports;
using WeChatVoice.Infrastructure.Materialization;
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
        VerifiedMaterialization materialization;
        try
        {
            materialization = await backend.MaterializeAsync(
                snapshot,
                new MaterializationOptions(Path.GetFullPath(outputRoot)),
                cancellationToken).ConfigureAwait(false);
        }
        catch (AppFailureException)
        {
            throw;
        }
        catch (DatabaseMaterializationException exception)
        {
            throw new AppFailureException(
                ErrorCode.MaterializationInvalid,
                "The external materialization backend failed validation.",
                exception);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new AppFailureException(
                ErrorCode.SnapshotInconsistent,
                "A verified Snapshot file disappeared before materialization completed.",
                exception);
        }
        MaterializationStateLock? stateLock = null;
        var operationId = Guid.NewGuid().ToString("N");
        try
        {
            stateLock = await MaterializationStateStore.AcquireLockAsync(outputRoot, cancellationToken).ConfigureAwait(false);
            var binding = await MaterializationStateStore.ReadManifestBindingAsync(outputRoot, cancellationToken).ConfigureAwait(false);
            var localWorkspace = await workspaceCreator.CreateAsync(
                materialization,
                confirmedAccountId,
                SnapshotSourceIdentity.TryDerive(snapshot.Snapshot.Manifest.SourceDirectory, snapshot.Snapshot.Manifest.Files),
                cancellationToken).ConfigureAwait(false);
            var fullWorkspacePath = Path.GetFullPath(localWorkspacePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullWorkspacePath)!);
            await LocalWorkspaceDocumentStore.WriteAsync(fullWorkspacePath, localWorkspace, cancellationToken).ConfigureAwait(false);

            await MaterializationStateStore.TransitionAsync(
                outputRoot,
                [MaterializationCommitStates.DatabasesCommitted, MaterializationCommitStates.FailedRecoverable],
                MaterializationCommitStates.WorkspaceCommitted,
                operationId,
                failureCode: null,
                cancellationToken: cancellationToken,
                heldLock: stateLock!,
                binding: binding).ConfigureAwait(false);
            await MaterializationStateStore.TransitionAsync(
                outputRoot,
                [MaterializationCommitStates.WorkspaceCommitted],
                MaterializationCommitStates.Completed,
                operationId,
                failureCode: null,
                cancellationToken: cancellationToken,
                heldLock: stateLock!,
                binding: binding).ConfigureAwait(false);
            return new ExecutedMaterialization(fullWorkspacePath, null, materialization.Result.WorkspaceId);
        }
        catch (AppFailureException)
        {
            await MarkRecoverableAsync(outputRoot, operationId, stateLock).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException)
        {
            // Keep cancellation separate from validation failure. If the
            // database commit already exists, recovery will inspect it later;
            // do not rewrite its durable state merely because the caller
            // cancelled the request.
            throw;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            await MarkRecoverableAsync(outputRoot, operationId, stateLock).ConfigureAwait(false);
            throw new AppFailureException(
                ErrorCode.WorkspaceInvalid,
                "The materialized databases could not be committed as a verified Workspace.",
                exception);
        }
        catch (Exception)
        {
            await MarkRecoverableAsync(outputRoot, operationId, stateLock).ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (stateLock is not null)
            {
                await stateLock.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task MarkRecoverableAsync(
        string outputRoot,
        string operationId,
        MaterializationStateLock? stateLock)
    {
        try
        {
            if (stateLock is not null)
            {
                await MaterializationStateStore.TryTransitionToFailedRecoverableAsync(
                    outputRoot,
                    operationId,
                    ErrorCode.MaterializationInvalid.ToString(),
                    CancellationToken.None,
                    stateLock).ConfigureAwait(false);
            }
            else
            {
                await MaterializationStateStore.TryTransitionToFailedRecoverableAsync(
                    outputRoot,
                    operationId,
                    ErrorCode.MaterializationInvalid.ToString(),
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception stateException) when (stateException is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // Preserve the original typed materialization failure when the
            // recovery marker itself cannot be updated.
        }
    }
}
