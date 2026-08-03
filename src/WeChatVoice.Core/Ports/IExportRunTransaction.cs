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

    /// <summary>Persists the finalized private entry before artifact publish.</summary>
    Task RecordEntryAsync(VoiceExportEntry entry, CancellationToken cancellationToken);

    /// <summary>
    /// Removes a staged item that failed before it became a committed export
    /// entry. Failed work must not remain in the durable transaction document
    /// as an unresolved artifact that blocks crash recovery.
    /// </summary>
    Task DiscardItemAsync(string messageId, CancellationToken cancellationToken);

    Task CommitAsync(CancellationToken cancellationToken);

    Task RollbackAsync(CancellationToken cancellationToken);
}
