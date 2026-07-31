using WeChatVoice.Core.Models;

namespace WeChatVoice.Core.Ports;

/// <summary>
/// Owns export-root layout, path containment, item leases, and manifest
/// persistence. Application code never receives physical output paths.
/// </summary>
public interface IVoiceExportStore
{
    ValueTask<IExportItemLease> BeginItemAsync(
        VoiceRecord record,
        ExportExistingPolicy policy,
        CancellationToken cancellationToken);

    Task FinalizeRunAsync(VoiceExportManifest manifest, CancellationToken cancellationToken);

    [Obsolete("Use BeginItemAsync and FinalizeRunAsync.")]
    ValueTask<VoiceExportPaths> CreatePathsAsync(VoiceMessage message, CancellationToken cancellationToken)
        => throw new NotSupportedException("This export store exposes the lease API only.");

    [Obsolete("Use FinalizeRunAsync.")]
    Task WriteManifestAsync(VoiceExportManifest manifest, CancellationToken cancellationToken)
        => FinalizeRunAsync(manifest, cancellationToken);
}
