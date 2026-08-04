using System.Security.Cryptography;
using System.Text;
using WeChatVoice.Core.Models;
using WeChatVoice.Infrastructure.Export;

namespace WeChatVoice.Tests;

public sealed class ExportTransactionJournalTests
{
    [Fact]
    public async Task Item_events_are_replayed_from_a_bounded_wal_tail()
    {
        using var temporary = new TestTemporaryDirectory();
        var transactionPath = temporary.GetPath("runs", "run.transaction.json");
        var initial = new ExportTransactionDocument(
            "run",
            "operation",
            "selection",
            ExportTransactionState.Staging,
            DateTimeOffset.UtcNow);
        var item = CreateItem("source-key");
        var current = initial;
        var journal = new ExportTransactionJournal(transactionPath);

        await journal.InitializeAsync(() => current, CancellationToken.None);
        current = new ExportTransactionDocument(
            initial.RunId,
            initial.OperationId,
            initial.SelectionFingerprint,
            initial.State,
            DateTimeOffset.UtcNow,
            [item]);
        await journal.AppendAsync(
            new ExportTransactionWalEvent(
                initial.RunId,
                initial.OperationId,
                initial.SelectionFingerprint,
                "item-upserted",
                DateTimeOffset.UtcNow,
                item.TransactionKey,
                item),
            () => current,
            compactAfterAppend: false,
            CancellationToken.None);

        var walPath = Path.Combine(
            Path.GetDirectoryName(transactionPath)!,
            "run.transaction.jsonl");
        Assert.NotEmpty(await File.ReadAllTextAsync(walPath));

        var recovered = await ExportTransactionJournal.ReadAsync(transactionPath, CancellationToken.None);
        var recoveredItem = Assert.Single(recovered.Items);
        Assert.Equal(item.TransactionKey, recoveredItem.TransactionKey);
        Assert.Equal(item.OriginalSha256, recoveredItem.OriginalSha256);
    }

    private static ExportTransactionItem CreateItem(string sourceStableKey)
    {
        var key = ExportItemTransactionKey.Compute(sourceStableKey);
        var sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceStableKey))).ToLowerInvariant();
        return new ExportTransactionItem(
            "message",
            sourceStableKey,
            "runs/.run.staging/original/aa/bb/item.silk",
            "original/aa/bb/item.silk",
            null,
            null,
            sourceStableKey.Length,
            sha256,
            null,
            null,
            ExportPublishState.NotStarted,
            ExportPublishState.NotStarted,
            ExportArtifactState.Missing,
            ExportArtifactState.Missing,
            TransactionKey: key,
            ItemState: ExportTransactionItemState.PayloadCommitted);
    }
}
