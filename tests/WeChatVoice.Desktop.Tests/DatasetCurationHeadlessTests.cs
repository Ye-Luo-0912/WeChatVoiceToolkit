using System.Security.Cryptography;
using Avalonia;
using Avalonia.Headless;
using WeChatVoice.Core.Models;
using WeChatVoice.Desktop.Infrastructure;
using WeChatVoice.Desktop.ViewModels;
using WeChatVoice.Desktop.Views;
using WeChatVoice.Infrastructure.Export;
using WeChatVoice.Workflows.Composition;
using WeChatVoice.Workflows.Workflows;

namespace WeChatVoice.Desktop.Tests;

public sealed class DatasetCurationHeadlessTests
{
    [Fact]
    public async Task Curation_page_loads_selects_builds_and_verifies_a_curated_dataset()
    {
        using var temporary = new TestTemporaryDirectory();
        var exportRoot = await CreateCommittedExportAsync(temporary);

        await using var services = new DesktopServices(
            new WorkflowCompositionRoot(
                new TestDoubles.SilentConfirmation(),
                datasetCuration: new DatasetCurationWorkflow()),
            new DesktopLog(temporary.Root),
            new RecentWorkspaceStore(temporary.Root),
            invokeOnUi: InvokeOnHeadlessUiAsync);
        services.Project.ExportDirectory = exportRoot;
        services.Project.LastExportRun = new VoiceExportWorkflowResult(
            new VoiceExportManifest(DateTimeOffset.UtcNow, RunId: "ui-export"),
            TestDoubles.Verified());

        var viewModel = HeadlessTestHost.Dispatch(() =>
        {
            var viewModel = new DatasetCurationViewModel(services);
            var view = new DatasetCurationView { DataContext = viewModel };
            Assert.NotNull(view);
            Assert.True(viewModel.CanNavigate);
            return viewModel;
        });

        Task? loadTask = null;
        HeadlessTestHost.Dispatch(() => { loadTask = viewModel.LoadCommand.ExecuteAsync(null); });
        await loadTask!;
        HeadlessTestHost.Dispatch(() =>
        {
            var loaded = Assert.Single(viewModel.Items);
            Assert.False(loaded.IsSelected);
            loaded.IsSelected = true;
        });
        Assert.Equal(1, viewModel.SelectedCount);
        Assert.Equal(100, viewModel.SelectedDurationMs);

        Task? saveTask = null;
        HeadlessTestHost.Dispatch(() => { saveTask = viewModel.SaveProfileCommand.ExecuteAsync(null); });
        await saveTask!;
        Task? buildTask = null;
        HeadlessTestHost.Dispatch(() => { buildTask = viewModel.BuildDatasetCommand.ExecuteAsync(null); });
        await buildTask!;
        Assert.False(string.IsNullOrWhiteSpace(viewModel.DatasetOutputDirectory));
        Assert.True(File.Exists(Path.Combine(viewModel.DatasetOutputDirectory!, "dataset.csv")));

        Task? verifyTask = null;
        HeadlessTestHost.Dispatch(() => { verifyTask = viewModel.VerifyDatasetCommand.ExecuteAsync(null); });
        await verifyTask!;
        Assert.Contains("验证通过", viewModel.BuildSummary, StringComparison.Ordinal);
    }

    private static async Task<string> CreateCommittedExportAsync(TestTemporaryDirectory temporary)
    {
        var root = temporary.GetPath("export");
        var bytes = new byte[] { 1, 2, 3, 4 };
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var record = new VoiceRecord(
            "message",
            "conversation",
            DateTimeOffset.UtcNow,
            VoiceDirection.Incoming,
            new VoicePayloadLocator("media", 0, "blob"),
            SnapshotId: "snapshot",
            AdapterId: "adapter",
            AccountId: "account",
            DataSetId: "dataset",
            AdapterVersion: "1",
            DatabaseFingerprints: ["database"],
            AdapterFamily: "adapter",
            AccountStableId: "account",
            ConversationStableId: "conversation",
            MessagePrimaryKey: "message",
            MediaPrimaryKey: "media:0:blob",
            PayloadSha256: hash,
            PayloadByteLength: bytes.Length);

        var store = new FileSystemVoiceExportStore(root);
        VoiceExportEntry entry;
        await using (var item = await store.BeginItemAsync(record, ExistingArtifactPolicy.Replace, CancellationToken.None))
        {
            await using (var output = await item.OpenOriginalWriteAsync(CancellationToken.None))
            {
                await output.WriteAsync(bytes);
            }

            var artifact = await item.CommitOriginalAsync(CancellationToken.None);
            entry = new VoiceExportEntry(
                record.MessageId,
                record.ConversationId,
                record.OccurredAtUtc,
                record.Direction,
                artifact.RelativePath,
                artifact.ByteLength,
                artifact.Sha256,
                null,
                record.SourceStableKey,
                DurationMs: 100);
        }

        var context = new VoiceCatalogContext("dataset", "adapter", "1", "account", ["database"]);
        await using (var journal = await store.BeginRunAsync(
            new VoiceExportRunContext("ui-export", context, DateTimeOffset.UtcNow),
            CancellationToken.None))
        {
            await journal.AppendAsync(new VoiceExportJournalEvent("run-started", "ui-export", DateTimeOffset.UtcNow, Context: context), CancellationToken.None);
            await journal.AppendAsync(new VoiceExportJournalEvent("item-committed", "ui-export", DateTimeOffset.UtcNow, entry.MessageId, Entry: entry), CancellationToken.None);
            await journal.AppendAsync(new VoiceExportJournalEvent("processing-completed", "ui-export", DateTimeOffset.UtcNow, Context: context), CancellationToken.None);
            await journal.FinalizeAsync(new VoiceExportManifest(DateTimeOffset.UtcNow, RunId: "ui-export"), CancellationToken.None);
        }

        return root;
    }

    private static Task InvokeOnHeadlessUiAsync(Action action)
        => HeadlessTestHost.DispatchOnUiAsync(action);

    private sealed class TestTemporaryDirectory : IDisposable
    {
        public TestTemporaryDirectory()
        {
            Root = Path.Combine(Path.GetTempPath(), "WeChatVoiceToolkit.CurationHeadless", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string GetPath(string relativePath) => Path.Combine(Root, relativePath);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
