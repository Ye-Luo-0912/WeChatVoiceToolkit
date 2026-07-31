using WeChatVoice.Core.Models;

namespace WeChatVoice.Core.Ports;

/// <summary>
/// Owns export-root layout, path containment, and manifest persistence. Path
/// creation must make parent directories available and reserve unique names,
/// but must not create the output files themselves.
/// </summary>
public interface IVoiceExportStore
{
    ValueTask<VoiceExportPaths> CreatePathsAsync(VoiceMessage message, CancellationToken cancellationToken);

    Task WriteManifestAsync(VoiceExportManifest manifest, CancellationToken cancellationToken);
}
