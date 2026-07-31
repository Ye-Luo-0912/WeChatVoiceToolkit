using WeChatVoice.Core.Models;
using WeChatVoice.KeyAcquisition.Models;

namespace WeChatVoice.KeyAcquisition.Ports;

/// <summary>
/// Internal high-sensitivity boundary. Implementations consume validated,
/// database-group-bound key material directly and must never persist a key
/// file or include key bytes in diagnostics.
/// </summary>
public interface IEphemeralDatabaseMaterializer
{
    string BackendId { get; }

    IReadOnlySet<string> SupportedEncryptionProfileIds { get; }

    Task<VerifiedMaterialization> MaterializeAsync(
        VerifiedRawSnapshot snapshot,
        VerifiedKeyAcquisition acquisition,
        MaterializationOptions options,
        CancellationToken cancellationToken);
}

public interface IEphemeralAcquireAndMaterializeService
{
    Task<VerifiedMaterialization> ExecuteAsync(
        VerifiedRawSnapshot snapshot,
        KeyAcquisitionOptions acquisitionOptions,
        MaterializationOptions materializationOptions,
        CancellationToken cancellationToken);
}
