using WeChatVoice.Core.Models;

namespace WeChatVoice.Core.Ports;

/// <summary>
/// Creates a file-level snapshot suitable for subsequent read-only inspection.
/// </summary>
public interface ISnapshotCreator
{
    Task<SnapshotManifest> CreateAsync(SnapshotRequest request, CancellationToken cancellationToken);
}
