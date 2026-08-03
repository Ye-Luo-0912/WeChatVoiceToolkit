using WeChatVoice.Core.Models;

namespace WeChatVoice.Core.Ports;

/// <summary>
/// Run-scoped staging boundary for export artifacts. Item leases returned by
/// <see cref="StageItemAsync"/> may write only to the run staging area until
/// <see cref="CommitAsync"/> succeeds.
/// </summary>
public interface IExportRunTransaction
{
    string RunId { get; }

    ValueTask<IExportItemLease> StageItemAsync(
        VoiceRecord record,
        ExistingArtifactPolicy policy,
        CancellationToken cancellationToken);

    Task CommitAsync(CancellationToken cancellationToken);

    Task RollbackAsync(CancellationToken cancellationToken);
}
