using WeChatVoice.Core.Models;

namespace WeChatVoice.Core.Ports;

/// <summary>Resolves duration only from a verified payload and validated PCM WAV output.</summary>
public interface IVoiceDurationResolver
{
    Task<long?> ResolveAsync(IVoiceCatalog catalog, VoiceRecord record, CancellationToken cancellationToken);
}
