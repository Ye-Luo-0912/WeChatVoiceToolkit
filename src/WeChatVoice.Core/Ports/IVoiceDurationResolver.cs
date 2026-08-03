using WeChatVoice.Core.Models;

namespace WeChatVoice.Core.Ports;

/// <summary>Resolves duration only from a verified payload and validated PCM WAV output.</summary>
public interface IVoiceDurationResolver
{
    Task<long?> ResolveAsync(IVoiceCatalog catalog, VoiceRecord record, CancellationToken cancellationToken);
}

/// <summary>
/// Optional streaming boundary for duration resolvers. Implementations consume
/// the supplied verified payload stream instead of reopening the catalog BLOB;
/// wrappers can therefore hash and decode the same source read.
/// </summary>
public interface IVoiceStreamDurationResolver
{
    Task<long?> ResolveAsync(Stream payload, CancellationToken cancellationToken);
}
