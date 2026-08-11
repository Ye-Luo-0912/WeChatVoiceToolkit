using System.CommandLine;
using WeChatVoice.Core.Models;
using WeChatVoice.Workflows.Workflows;

namespace WeChatVoice.Cli;

internal static partial class CliApplication
{
    static Command CreateStorageCommand()
    {
        var storageCommand = new Command("storage", "Inspect and reclaim application-owned storage.");
        var inventoryCommand = new Command("inventory", "Report a read-only inventory of app-owned storage.");
        var previewCommand = new Command("preview", "Preview what a cleanup would reclaim without deleting anything.");
        var cleanupCommand = new Command("cleanup", "Reclaim independent transient and expired-recoverable app-owned storage.");

        var appDataOption = new Option<string?>("--app-data")
        {
            Description = "Optional app data root to scan instead of the default LocalApplicationData\\WeChatVoiceToolkit.",
        };
        inventoryCommand.Options.Add(appDataOption);
        inventoryCommand.SetAction(async (parseResult, cancellationToken) =>
        {
            try
            {
                await using var composition = CreateRoot();
                var context = new WorkflowContext(composition.AccountConfirmation, new Progress<OperationProgress>(ReportProgress));
                var summary = await composition.StorageLifecycle.InventoryAsync(
                    new StorageInventoryRequest(parseResult.GetValue(appDataOption)),
                    context,
                    cancellationToken).ConfigureAwait(false);
                WriteJson(ToInventoryReport(summary));
                return 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Console.Error.WriteLine("Storage inventory was cancelled.");
                return 130;
            }
            catch (Exception exception)
            {
                WriteError(exception);
                return 1;
            }
        });

        previewCommand.Options.Add(appDataOption);
        previewCommand.SetAction(async (parseResult, cancellationToken) =>
        {
            try
            {
                await using var composition = CreateRoot();
                var context = new WorkflowContext(composition.AccountConfirmation, new Progress<OperationProgress>(ReportProgress));
                var preview = await composition.StorageLifecycle.PreviewCleanupAsync(
                    new StorageCleanupRequest(),
                    context,
                    cancellationToken).ConfigureAwait(false);
                WriteJson(new StoragePreviewReport(preview.ItemCount, preview.TotalBytes,
                    preview.Items.Select(static item => new StorageItemReport(
                        item.Kind,
                        item.Path,
                        item.TotalBytes,
                        item.LastModifiedUtc,
                        item.HasActiveLock,
                        item.Note)).ToArray()));
                return 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Console.Error.WriteLine("Storage preview was cancelled.");
                return 130;
            }
            catch (Exception exception)
            {
                WriteError(exception);
                return 1;
            }
        });

        var forceOption = new Option<bool>("--force-recoverable")
        {
            Description = "Reclaim recoverable workspaces even before their retention window expires.",
        };
        cleanupCommand.Options.Add(appDataOption);
        cleanupCommand.Options.Add(forceOption);
        cleanupCommand.SetAction(async (parseResult, cancellationToken) =>
        {
            try
            {
                await using var composition = CreateRoot();
                var context = new WorkflowContext(composition.AccountConfirmation, new Progress<OperationProgress>(ReportProgress));
                var result = await composition.StorageLifecycle.CleanupAsync(
                    new StorageCleanupRequest(ForceRecoverable: parseResult.GetValue(forceOption)),
                    context,
                    cancellationToken).ConfigureAwait(false);
                WriteJson(new StorageCleanupReport(result.DeletedCount, result.DeletedBytes, result.SkippedReasons.ToArray()));
                return 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Console.Error.WriteLine("Storage cleanup was cancelled.");
                return 130;
            }
            catch (Exception exception)
            {
                WriteError(exception);
                return 1;
            }
        });

        storageCommand.Subcommands.Add(inventoryCommand);
        storageCommand.Subcommands.Add(previewCommand);
        storageCommand.Subcommands.Add(cleanupCommand);
        return storageCommand;
    }

    private static InventoryReport ToInventoryReport(StorageInventorySummary summary) =>
        new(
            summary.SnapshotBytes,
            summary.WorkspaceBytes,
            summary.ExportBytes,
            summary.DatasetBytes,
            summary.TempBytes,
            summary.RecoverableBytes,
            summary.SafelyReclaimableBytes,
            summary.Assets.Select(static item => new StorageItemReport(
                item.Kind,
                item.Path,
                item.TotalBytes,
                item.LastModifiedUtc,
                item.HasActiveLock,
                item.Note)).ToArray());

    internal sealed record InventoryReport(
        long SnapshotBytes,
        long WorkspaceBytes,
        long ExportBytes,
        long DatasetBytes,
        long TempBytes,
        long RecoverableBytes,
        long SafelyReclaimableBytes,
        IReadOnlyList<StorageItemReport> Items);

    internal sealed record StoragePreviewReport(int ItemCount, long TotalBytes, IReadOnlyList<StorageItemReport> Items);

    internal sealed record StorageCleanupReport(int DeletedCount, long DeletedBytes, IReadOnlyList<string> SkippedReasons);

    internal sealed record StorageItemReport(
        StorageAssetKind Kind,
        string Path,
        long TotalBytes,
        DateTimeOffset LastModifiedUtc,
        bool HasActiveLock,
        string? Note);
}