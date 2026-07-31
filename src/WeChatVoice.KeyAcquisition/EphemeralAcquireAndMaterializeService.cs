using WeChatVoice.Core.Models;
using WeChatVoice.KeyAcquisition.Models;
using WeChatVoice.KeyAcquisition.Ports;

namespace WeChatVoice.KeyAcquisition;

/// <summary>
/// Owns the complete sensitive lifetime: acquire, materialize, and always
/// dispose/zero all key bindings before returning a non-sensitive result.
/// </summary>
public sealed class EphemeralAcquireAndMaterializeService(
    IKeyAcquisitionService acquisitionService,
    IEphemeralDatabaseMaterializer materializer) : IEphemeralAcquireAndMaterializeService
{
    private readonly IKeyAcquisitionService acquisitionService = acquisitionService ?? throw new ArgumentNullException(nameof(acquisitionService));
    private readonly IEphemeralDatabaseMaterializer materializer = materializer ?? throw new ArgumentNullException(nameof(materializer));

    public async Task<VerifiedMaterialization> ExecuteAsync(
        VerifiedRawSnapshot snapshot,
        KeyAcquisitionOptions acquisitionOptions,
        MaterializationOptions materializationOptions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(acquisitionOptions);
        ArgumentNullException.ThrowIfNull(materializationOptions);

        if (!string.Equals(acquisitionOptions.ProfileId, materializer.EncryptionProfileId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The requested encryption Profile is not accepted by the materializer.");
        }

        using var acquisition = await acquisitionService.AcquireAsync(
            snapshot,
            acquisitionOptions,
            cancellationToken).ConfigureAwait(false);

        if (!string.Equals(acquisition.SnapshotId, snapshot.Snapshot.SnapshotId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The acquired keys are not bound to the verified SnapshotId.");
        }

        var materialization = await materializer.MaterializeAsync(
            snapshot,
            acquisition,
            materializationOptions,
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(materialization.Result.SourceSnapshotId, snapshot.Snapshot.SnapshotId, StringComparison.Ordinal)
            || !string.Equals(materialization.Result.BackendId, materializer.BackendId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The materializer returned a result with an unexpected SnapshotId or BackendId.");
        }

        return materialization;
    }
}
