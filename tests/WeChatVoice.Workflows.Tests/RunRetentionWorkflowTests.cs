using System.Text.Json;
using WeChatVoice.Core.Models;
using WeChatVoice.Core.Ports;
using WeChatVoice.Workflows.Workflows;

namespace WeChatVoice.Workflows.Tests;

/// <summary>
/// Covers the run / metadata retention workflow. Preview is read-only and never
/// deletes; compact removes only the journal and transaction metadata of older
/// unreferenced runs while always retaining the committed manifests and
/// metadata-commit descriptor. A run bound to a dataset selection profile is
/// never compacted, and incomplete runs are always retained.
/// </summary>
public sealed class RunRetentionWorkflowTests
{
    private static readonly JsonSerializerOptions CamelCase = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Preview_classifies_recent_complete_runs_and_older_compactable_runs()
    {
        using var temp = new RunTemp();
        temp.CreateCompleteRun("run-old", minutesAgo: 120);
        temp.CreateCompleteRun("run-mid", minutesAgo: 60);
        temp.CreateCompleteRun("run-new", minutesAgo: 5);
        var workflow = new RunRetentionWorkflow();

        var preview = await workflow.PreviewAsync(
            new RunRetentionOptions(temp.ExportRoot, KeepRecent: 2),
            Context(),
            CancellationToken.None);

        Assert.Equal(3, preview.Items.Count);
        var old = Assert.Single(preview.Items, item => item.RunId == "run-old");
        Assert.Equal(RunRetentionDisposition.Compactable, old.Disposition);
        var mid = Assert.Single(preview.Items, item => item.RunId == "run-mid");
        Assert.Equal(RunRetentionDisposition.KeepRecent, mid.Disposition);
        var newest = Assert.Single(preview.Items, item => item.RunId == "run-new");
        Assert.Equal(RunRetentionDisposition.KeepRecent, newest.Disposition);
        Assert.Equal(1, preview.CompactableCount);
        Assert.True(preview.CompactableBytes >= old.JournalBytes + old.TransactionBytes);
    }

    [Fact]
    public async Task Preview_marks_referenced_run_as_never_compactable()
    {
        using var temp = new RunTemp();
        temp.CreateCompleteRun("run-referenced", minutesAgo: 120);
        temp.CreateCompleteRun("run-new", minutesAgo: 5);
        temp.WriteSelectionProfile(referencedRunId: "run-referenced");
        var workflow = new RunRetentionWorkflow();

        var preview = await workflow.PreviewAsync(
            new RunRetentionOptions(temp.ExportRoot, KeepRecent: 1),
            Context(),
            CancellationToken.None);

        var referenced = Assert.Single(preview.Items, item => item.RunId == "run-referenced");
        Assert.Equal(RunRetentionDisposition.Referenced, referenced.Disposition);
        Assert.DoesNotContain(preview.Items, item => item.Disposition == RunRetentionDisposition.Compactable);
    }

    [Fact]
    public async Task Preview_keeps_incomplete_run_journal()
    {
        using var temp = new RunTemp();
        temp.CreateIncompleteRun("run-incomplete", minutesAgo: 120);
        temp.CreateCompleteRun("run-new", minutesAgo: 5);
        var workflow = new RunRetentionWorkflow();

        var preview = await workflow.PreviewAsync(
            new RunRetentionOptions(temp.ExportRoot, KeepRecent: 1),
            Context(),
            CancellationToken.None);

        var incomplete = Assert.Single(preview.Items, item => item.RunId == "run-incomplete");
        Assert.Equal(RunRetentionDisposition.KeepRecent, incomplete.Disposition);
        Assert.False(incomplete.IsComplete);
    }

    [Fact]
    public async Task Preview_never_deletes_anything()
    {
        using var temp = new RunTemp();
        var oldRun = temp.CreateCompleteRun("run-old", minutesAgo: 120);
        var workflow = new RunRetentionWorkflow();

        var preview = await workflow.PreviewAsync(
            new RunRetentionOptions(temp.ExportRoot, KeepRecent: 0),
            Context(),
            CancellationToken.None);

        Assert.Single(preview.Items, item => item.Disposition == RunRetentionDisposition.Compactable);
        Assert.True(File.Exists(oldRun.Journal));
        Assert.True(File.Exists(oldRun.Transaction));
        Assert.True(File.Exists(oldRun.MetadataCommit));
    }

    [Fact]
    public async Task Compact_removes_journal_and_transaction_but_retains_manifests()
    {
        using var temp = new RunTemp();
        var oldRun = temp.CreateCompleteRun("run-old", minutesAgo: 120);
        var newRun = temp.CreateCompleteRun("run-new", minutesAgo: 5);
        var workflow = new RunRetentionWorkflow();

        var result = await workflow.CompactAsync(
            new RunRetentionOptions(temp.ExportRoot, KeepRecent: 1),
            Context(),
            CancellationToken.None);

        Assert.Equal(1, result.CompactedCount);
        Assert.False(File.Exists(oldRun.Journal));
        Assert.False(File.Exists(oldRun.Transaction));
        Assert.False(File.Exists(oldRun.Checkpoint));
        Assert.False(File.Exists(oldRun.Wal));
        // Committed metadata is always retained.
        Assert.True(File.Exists(oldRun.MetadataCommit));
        Assert.True(File.Exists(oldRun.PrivateManifest));
        Assert.True(File.Exists(oldRun.PortableManifest));
        Assert.True(File.Exists(oldRun.ArtifactIndex));
        // The recent run is untouched.
        Assert.True(File.Exists(newRun.Journal));
        Assert.True(File.Exists(newRun.Transaction));
    }

    [Fact]
    public async Task Compact_skips_referenced_run()
    {
        using var temp = new RunTemp();
        var oldRun = temp.CreateCompleteRun("run-old", minutesAgo: 120);
        temp.WriteSelectionProfile(referencedRunId: "run-old");
        var workflow = new RunRetentionWorkflow();

        var result = await workflow.CompactAsync(
            new RunRetentionOptions(temp.ExportRoot, KeepRecent: 0),
            Context(),
            CancellationToken.None);

        Assert.Equal(0, result.CompactedCount);
        Assert.True(File.Exists(oldRun.Journal));
        Assert.True(File.Exists(oldRun.Transaction));
    }

    [Fact]
    public async Task Compact_with_no_runs_returns_empty_result()
    {
        using var temp = new RunTemp();
        var workflow = new RunRetentionWorkflow();

        var result = await workflow.CompactAsync(
            new RunRetentionOptions(temp.ExportRoot, KeepRecent: 5),
            Context(),
            CancellationToken.None);

        Assert.Equal(0, result.CompactedCount);
        Assert.Equal(0, result.CompactedBytes);
        Assert.Empty(result.SkippedReasons);
    }

    private static WorkflowContext Context() => new(new TestConfirmation());

    private sealed class TestConfirmation : IAccountConfirmation
    {
        public Task<AccountConfirmation> ConfirmAsync(AccountIdentityReport report, CancellationToken cancellationToken)
            => Task.FromResult(new AccountConfirmation(true, report.AccountCandidate));
    }

    private sealed class RunTemp : IDisposable
    {
        public RunTemp()
        {
            Root = Path.Combine(Path.GetTempPath(), "WeChatVoiceToolkit.RunRetentionTests", Guid.NewGuid().ToString("N"));
            ExportRoot = Path.Combine(Root, "export");
            Directory.CreateDirectory(Path.Combine(ExportRoot, "runs"));
        }

        public string Root { get; }
        public string ExportRoot { get; }

        public RunFiles CreateCompleteRun(string runId, int minutesAgo)
        {
            var files = new RunFiles(ExportRoot, runId);
            var manifest = new VoiceExportManifest(
                GeneratedAtUtc: DateTimeOffset.UtcNow.AddMinutes(-minutesAgo),
                Entries: [],
                RunId: runId);
            File.WriteAllText(files.PrivateManifest, JsonSerializer.Serialize(manifest, CamelCase));
            File.WriteAllBytes(files.Journal, new byte[64]);
            File.WriteAllBytes(files.Transaction, new byte[32]);
            File.WriteAllBytes(files.Checkpoint, new byte[16]);
            File.WriteAllBytes(files.Wal, new byte[8]);
            File.WriteAllText(files.MetadataCommit, "{\"RunId\":\"" + runId + "\"}");
            File.WriteAllText(files.PortableManifest, "{}");
            File.WriteAllText(files.Csv, "item-id\n");
            File.WriteAllText(files.ArtifactIndex, "[]\n");
            return files;
        }

        public RunFiles CreateIncompleteRun(string runId, int minutesAgo)
        {
            var files = new RunFiles(ExportRoot, runId);
            File.WriteAllBytes(files.Journal, new byte[64]);
            File.WriteAllBytes(files.Transaction, new byte[32]);
            return files;
        }

        public void WriteSelectionProfile(string referencedRunId)
        {
            var profile = new DatasetSelectionProfile(
                ManifestSha256: new string('a', 64),
                RunId: referencedRunId);
            File.WriteAllText(
                Path.Combine(ExportRoot, "selection-profile.json"),
                JsonSerializer.Serialize(profile, CamelCase));
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private sealed class RunFiles
    {
        public RunFiles(string exportRoot, string runId)
        {
            var runs = Path.Combine(exportRoot, "runs");
            Journal = Path.Combine(runs, runId + ".jsonl");
            Transaction = Path.Combine(runs, runId + ".transaction.json");
            Checkpoint = Path.Combine(runs, runId + ".transaction.checkpoint.json");
            Wal = Path.Combine(runs, runId + ".transaction.jsonl");
            MetadataCommit = Path.Combine(runs, runId + ".metadata-commit.json");
            PrivateManifest = Path.Combine(runs, runId + ".manifest.private.json");
            PortableManifest = Path.Combine(runs, runId + ".dataset.manifest.json");
            Csv = Path.Combine(runs, runId + ".dataset.csv");
            ArtifactIndex = Path.Combine(runs, runId + ".artifact-index.jsonl");
        }

        public string Journal { get; }
        public string Transaction { get; }
        public string Checkpoint { get; }
        public string Wal { get; }
        public string MetadataCommit { get; }
        public string PrivateManifest { get; }
        public string PortableManifest { get; }
        public string Csv { get; }
        public string ArtifactIndex { get; }
    }
}
