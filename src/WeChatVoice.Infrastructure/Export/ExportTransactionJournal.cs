using System.Text;
using System.Text.Json;
using WeChatVoice.Core.Models;
using WeChatVoice.Infrastructure.Serialization;

namespace WeChatVoice.Infrastructure.Export;

/// <summary>
/// Durable transaction metadata persistence. Item changes are appended as
/// bounded WAL events; a checkpoint is written every few hundred events or
/// when the tail grows large. Completed/recovered runs are compacted back to
/// the compatibility transaction document.
/// </summary>
internal sealed class ExportTransactionJournal
{
    private const int CheckpointEventLimit = 256;
    private const long CheckpointByteLimit = 16L * 1024 * 1024;

    private readonly string _transactionPath;
    private readonly string _walPath;
    private readonly string _checkpointPath;
    private int _eventsSinceCheckpoint;
    private long _bytesSinceCheckpoint;

    public ExportTransactionJournal(string transactionPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionPath);
        _transactionPath = Path.GetFullPath(transactionPath);
        _walPath = GetWalPath(_transactionPath);
        _checkpointPath = GetCheckpointPath(_transactionPath);
    }

    public async Task InitializeAsync(
        Func<ExportTransactionDocument> snapshotFactory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshotFactory);
        await WriteSnapshotAsync(snapshotFactory(), cancellationToken).ConfigureAwait(false);
    }

    public async Task AppendAsync(
        ExportTransactionWalEvent walEvent,
        Func<ExportTransactionDocument> snapshotFactory,
        bool compactAfterAppend,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(walEvent);
        ArgumentNullException.ThrowIfNull(snapshotFactory);
        var serialized = JsonSerializer.Serialize(walEvent, InfrastructureJson.Compact);
        await DurableJsonlJournalWriter.AppendRawJsonLinesAsync(
            _walPath,
            [serialized],
            InfrastructureJson.Compact,
            cancellationToken).ConfigureAwait(false);
        _eventsSinceCheckpoint++;
        _bytesSinceCheckpoint = checked(_bytesSinceCheckpoint + Encoding.UTF8.GetByteCount(serialized) + 1);

        if (compactAfterAppend
            || _eventsSinceCheckpoint >= CheckpointEventLimit
            || _bytesSinceCheckpoint >= CheckpointByteLimit)
        {
            await WriteSnapshotAsync(snapshotFactory(), cancellationToken).ConfigureAwait(false);
        }
    }

    public static async Task<ExportTransactionDocument> ReadAsync(
        string transactionPath,
        CancellationToken cancellationToken)
    {
        var fullTransactionPath = Path.GetFullPath(transactionPath);
        var checkpointPath = GetCheckpointPath(fullTransactionPath);
        var basePath = File.Exists(checkpointPath) ? checkpointPath : fullTransactionPath;
        var document = await ReadDocumentAsync(basePath, cancellationToken).ConfigureAwait(false);
        var walPath = GetWalPath(fullTransactionPath);
        if (!File.Exists(walPath) || new FileInfo(walPath).Length == 0)
        {
            return document;
        }

        await using var stream = new FileStream(
            walPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 64 * 1024);
        var walText = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        var walLines = walText.Split('\n');
        for (var lineIndex = 0; lineIndex < walLines.Length; lineIndex++)
        {
            var line = walLines[lineIndex].TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            ExportTransactionWalEvent? walEvent;
            try
            {
                walEvent = JsonSerializer.Deserialize<ExportTransactionWalEvent>(line, InfrastructureJson.Compact);
            }
            catch (JsonException exception)
            {
                // A partial final line is recoverable. A malformed line with
                // another line after it is a durable middle-record failure.
                var isFinalTruncatedLine = lineIndex == walLines.Length - 1 && !walText.EndsWith('\n');
                if (isFinalTruncatedLine)
                {
                    _ = exception;
                    break;
                }

                throw new InvalidDataException("An export transaction WAL contains a malformed middle event.", exception);
            }

            if (walEvent is null)
            {
                throw new InvalidDataException("An export transaction WAL contains an empty event.");
            }

            document = Apply(document, walEvent);
        }

        return document;
    }

    public static async Task WriteSnapshotAsync(
        string transactionPath,
        ExportTransactionDocument document,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        var fullTransactionPath = Path.GetFullPath(transactionPath);
        await AtomicFileWriter.WriteJsonAsync(
            GetCheckpointPath(fullTransactionPath),
            document,
            InfrastructureJson.Indented,
            cancellationToken).ConfigureAwait(false);
        await AtomicFileWriter.WriteJsonAsync(
            fullTransactionPath,
            document,
            InfrastructureJson.Indented,
            cancellationToken).ConfigureAwait(false);
        await AtomicFileWriter.WriteTextAsync(
            GetWalPath(fullTransactionPath),
            string.Empty,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteSnapshotAsync(
        ExportTransactionDocument document,
        CancellationToken cancellationToken)
    {
        await WriteSnapshotAsync(_transactionPath, document, cancellationToken).ConfigureAwait(false);
        _eventsSinceCheckpoint = 0;
        _bytesSinceCheckpoint = 0;
    }

    private static async Task<ExportTransactionDocument> ReadDocumentAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<ExportTransactionDocument>(
                   stream,
                   InfrastructureJson.Compact,
                   cancellationToken).ConfigureAwait(false)
               ?? throw new InvalidDataException($"The transaction document '{path}' is empty.");
    }

    private static ExportTransactionDocument Apply(
        ExportTransactionDocument document,
        ExportTransactionWalEvent walEvent)
    {
        if (!string.Equals(document.RunId, walEvent.RunId, StringComparison.Ordinal)
            || !string.Equals(document.OperationId, walEvent.OperationId, StringComparison.Ordinal)
            || !string.Equals(document.SelectionFingerprint, walEvent.SelectionFingerprint, StringComparison.Ordinal))
        {
            throw new InvalidDataException("An export transaction WAL event does not match its transaction document.");
        }

        var items = document.Items.ToList();
        switch (walEvent.Event)
        {
            case "item-upserted":
                if (walEvent.Item is null)
                {
                    throw new InvalidDataException("An export transaction item event has no item payload.");
                }

                var itemKey = walEvent.TransactionKey
                    ?? walEvent.Item.TransactionKey
                    ?? ExportItemTransactionKey.TryCompute(walEvent.Item.SourceStableKey)
                    ?? throw new InvalidDataException("An export transaction item event has no stable key.");
                var existingIndex = items.FindIndex(item => string.Equals(
                    item.TransactionKey ?? ExportItemTransactionKey.TryCompute(item.SourceStableKey),
                    itemKey,
                    StringComparison.Ordinal));
                if (existingIndex >= 0)
                {
                    items[existingIndex] = walEvent.Item;
                }
                else
                {
                    items.Add(walEvent.Item);
                }

                return Rebuild(document, items, walEvent.OccurredAtUtc);

            case "item-removed":
                if (string.IsNullOrWhiteSpace(walEvent.TransactionKey))
                {
                    throw new InvalidDataException("An export transaction removal event has no stable key.");
                }

                items.RemoveAll(item => string.Equals(
                    item.TransactionKey ?? ExportItemTransactionKey.TryCompute(item.SourceStableKey),
                    walEvent.TransactionKey,
                    StringComparison.Ordinal));
                return Rebuild(document, items, walEvent.OccurredAtUtc);

            case "state-changed":
                return new ExportTransactionDocument(
                    document.RunId,
                    document.OperationId,
                    document.SelectionFingerprint,
                    walEvent.State ?? document.State,
                    walEvent.OccurredAtUtc,
                    items,
                    walEvent.MetadataCommit ?? document.MetadataCommit,
                    walEvent.FailureCode ?? document.FailureCode,
                    document.Format,
                    walEvent.ExplicitRollback || document.ExplicitRollback);

            default:
                throw new InvalidDataException($"The export transaction WAL event '{walEvent.Event}' is unsupported.");
        }
    }

    private static ExportTransactionDocument Rebuild(
        ExportTransactionDocument document,
        IReadOnlyList<ExportTransactionItem> items,
        DateTimeOffset updatedAtUtc)
        => new(
            document.RunId,
            document.OperationId,
            document.SelectionFingerprint,
            document.State,
            updatedAtUtc,
            items,
            document.MetadataCommit,
            document.FailureCode,
            document.Format,
            document.ExplicitRollback);

    private static string GetWalPath(string transactionPath)
        => Path.Combine(
            Path.GetDirectoryName(transactionPath)!,
            Path.GetFileNameWithoutExtension(transactionPath) + ".jsonl");

    private static string GetCheckpointPath(string transactionPath)
        => Path.Combine(
            Path.GetDirectoryName(transactionPath)!,
            Path.GetFileNameWithoutExtension(transactionPath) + ".checkpoint.json");
}
