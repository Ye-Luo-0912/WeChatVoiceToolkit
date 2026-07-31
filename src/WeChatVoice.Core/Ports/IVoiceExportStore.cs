using WeChatVoice.Core.Models;

namespace WeChatVoice.Core.Ports;

/// <summary>
/// Owns export-root layout, path containment, item leases, and manifest
/// persistence. Application code never receives physical output paths.
/// </summary>
public interface IVoiceExportStore
{
    ValueTask<IExportRunJournal> BeginRunAsync(
        VoiceExportRunContext context,
        CancellationToken cancellationToken);

    ValueTask<IExportItemLease> BeginItemAsync(
        VoiceRecord record,
        ExistingArtifactPolicy policy,
        CancellationToken cancellationToken);

    Task FinalizeRunAsync(VoiceExportManifest manifest, CancellationToken cancellationToken);
}
