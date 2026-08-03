using WeChatVoice.Core.Models;

namespace WeChatVoice.Core.Ports;

public interface IExportRunLease : IExportRunTransaction, IAsyncDisposable
{
    Task AppendAsync(VoiceExportJournalEvent journalEvent, CancellationToken cancellationToken);

    Task FinalizeAsync(VoiceExportManifest manifest, CancellationToken cancellationToken);
}
