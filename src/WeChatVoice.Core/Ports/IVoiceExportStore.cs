using WeChatVoice.Core.Models;

namespace WeChatVoice.Core.Ports;

/// <summary>
/// Owns export-root layout, path containment, item leases, and manifest
/// persistence. Application code never receives physical output paths.
/// </summary>
public interface IVoiceExportStore
{
    ValueTask<IExportRunLease> BeginRunAsync(
        VoiceExportRunContext context,
        CancellationToken cancellationToken);

    ValueTask<IExportItemLease> BeginItemAsync(
        VoiceRecord record,
        ExistingArtifactPolicy policy,
        CancellationToken cancellationToken);

}
