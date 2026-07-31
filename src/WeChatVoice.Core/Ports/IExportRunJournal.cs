using WeChatVoice.Core.Models;

namespace WeChatVoice.Core.Ports;

public interface IExportRunJournal : IAsyncDisposable
{
    Task AppendAsync(VoiceExportJournalEvent journalEvent, CancellationToken cancellationToken);
}
