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

        using var acquisition = await acquisitionService.AcquireAsync(
            snapshot,
            acquisitionOptions,
            cancellationToken).ConfigureAwait(false);

        if (!string.Equals(acquisition.SnapshotId, snapshot.Snapshot.SnapshotId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The acquired keys are not bound to the verified SnapshotId.");
        }

        return await materializer.MaterializeAsync(
            snapshot,
            acquisition,
            materializationOptions,
            cancellationToken).ConfigureAwait(false);
    }
}
