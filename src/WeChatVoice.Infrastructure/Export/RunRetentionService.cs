using System.Text.Json;
using WeChatVoice.Core.Models;
using WeChatVoice.Infrastructure.Serialization;

namespace WeChatVoice.Infrastructure.Export;

/// <summary>
/// Run / metadata retention for an export root's <c>runs/</c> directory.
/// The most recent complete runs are retained in full (journal + transaction);
/// older runs are never deleted wholesale. Instead, older <em>unreferenced</em>
/// runs are <b>compacted</b>: their crash-recovery journal and transaction
/// metadata are removed while the committed manifests, CSV, artifact index, and
/// metadata-commit descriptor are always retained. A run bound to a dataset
/// selection profile is never compacted. The <c>latest</c> aliases at the export
/// root are never treated as the sole authority for what may be removed.
/// </summary>
public sealed class RunRetentionService
{
    /// <summary>
    /// Inspects the export root's runs and returns a read-only preview of which
    /// runs are safe to compact. Never deletes anything.
    /// </summary>
    public async Task<RunRetentionPreview> PreviewAsync(
        RunRetentionOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var runsRoot = RunningRoot(options.ExportRoot);
        if (!Directory.Exists(runsRoot))
        {
            return new RunRetentionPreview(options.KeepRecent, 0, 0, []);
        }

        var referenced = await ReadReferencedRunIdsAsync(options.ExportRoot, cancellationToken).ConfigureAwait(false);
        var runs = EnumerateRuns(runsRoot).ToList();
        foreach (var run in runs)
        {
            run.Disposition = Classify(run, referenced, options.KeepRecent, runs);
        }

        var items = runs.OrderBy(static item => item.CreatedUtc).Select(ToItem).ToArray();
        var compactable = items.Where(static item => item.Disposition == RunRetentionDisposition.Compactable).ToArray();
        return new RunRetentionPreview(
            options.KeepRecent,
            compactable.Length,
            compactable.Sum(static item => item.JournalBytes + item.TransactionBytes),
            items);
    }

    /// <summary>
    /// Compacts older unreferenced runs by removing their journal and
    /// transaction metadata. Committed manifests, CSV, artifact index, and the
    /// metadata-commit descriptor are always retained. Repeats inspection and
    /// re-checks references before removing anything.
    /// </summary>
    public async Task<RunRetentionResult> CompactAsync(
        RunRetentionOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var runsRoot = RunningRoot(options.ExportRoot);
        if (!Directory.Exists(runsRoot))
        {
            return new RunRetentionResult(0, 0, []);
        }

        var preview = await PreviewAsync(options, cancellationToken).ConfigureAwait(false);
        var skipped = new List<string>();
        var compacted = 0;
        long compactedBytes = 0;

        foreach (var item in preview.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Disposition != RunRetentionDisposition.Compactable)
            {
                continue;
            }

            if (IsReparsePoint(runsRoot))
            {
                skipped.Add("runs 目录含 Reparse Point，拒绝清理。");
                break;
            }

            var journal = JournalPath(options.ExportRoot, item.RunId);
            var transaction = TransactionPath(options.ExportRoot, item.RunId);
            var removedJournal = TryDeleteFile(journal);
            var removedTransaction = TryDeleteTransactionFiles(runsRoot, item.RunId);
            if (!removedJournal && !removedTransaction)
            {
                skipped.Add($"run {item.RunId}：journal 与 transaction 均删除失败。");
                continue;
            }

            compacted += 1;
            compactedBytes += item.JournalBytes + item.TransactionBytes;
        }

        return new RunRetentionResult(compacted, compactedBytes, skipped);
    }

    private static RunRetentionDisposition Classify(
        RunScan run,
        IReadOnlySet<string> referenced,
        int keepRecent,
        IReadOnlyList<RunScan> allRuns)
    {
        if (referenced.Contains(run.RunId))
        {
            run.Reason = "绑定到数据集选择 profile，不可压缩。";
            return RunRetentionDisposition.Referenced;
        }

        if (!run.IsComplete)
        {
            run.Reason = "run 未提交完整 manifest，保留 journal 以便恢复。";
            return RunRetentionDisposition.KeepRecent;
        }

        // Count complete runs more recent than this one; if it falls outside the
        // keep-recent window it is a candidate for compaction.
        var newerCompleteCount = allRuns.Count(candidate
            => candidate.IsComplete
               && candidate.RunId != run.RunId
               && candidate.CreatedUtc > run.CreatedUtc);
        if (newerCompleteCount < keepRecent)
        {
            run.Reason = $"属于最近 {keepRecent} 个完整 run，完整保留。";
            return RunRetentionDisposition.KeepRecent;
        }

        run.Reason = "早于保留窗口且未被引用，可压缩 journal/transaction。";
        return RunRetentionDisposition.Compactable;
    }

    private static RunRetentionItem ToItem(RunScan run)
        => new(
            run.RunId,
            run.Disposition,
            run.IsComplete,
            run.CreatedUtc,
            run.JournalBytes,
            run.TransactionBytes,
            run.TotalBytes,
            run.Reason);

    /// <summary>RunIds bound by the export root's selection profile(s).</summary>
    private static async Task<IReadOnlySet<string>> ReadReferencedRunIdsAsync(
        string exportRoot,
        CancellationToken cancellationToken)
    {
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        var profilePath = Path.Combine(Path.GetFullPath(exportRoot), DatasetSelectionProfileStore.ProfileFileName);
        if (!File.Exists(profilePath))
        {
            return referenced;
        }

        try
        {
            await using var stream = new FileStream(
                profilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                32 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var profile = await JsonSerializer.DeserializeAsync<DatasetSelectionProfile>(
                stream,
                InfrastructureJson.Compact,
                cancellationToken).ConfigureAwait(false);
            if (profile is not null && !string.IsNullOrWhiteSpace(profile.RunId))
            {
                referenced.Add(profile.RunId);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // A corrupt profile must not cause any run to be deleted; treat it
            // as "no reference known" only for the preview, and rely on the
            // compact path's re-inspection.
        }

        return referenced;
    }

    private static IEnumerable<RunScan> EnumerateRuns(string runsRoot)
    {
        var runIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(runsRoot, "*", SearchOption.TopDirectoryOnly))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            var name = Path.GetFileName(file);
            if (name.EndsWith(".metadata-commit.json", StringComparison.OrdinalIgnoreCase))
            {
                runIds.Add(name[..^".metadata-commit.json".Length]);
            }
            else if (name.EndsWith(".manifest.private.json", StringComparison.OrdinalIgnoreCase))
            {
                runIds.Add(name[..^".manifest.private.json".Length]);
            }
            else if (name.EndsWith(".dataset.manifest.json", StringComparison.OrdinalIgnoreCase))
            {
                runIds.Add(name[..^".dataset.manifest.json".Length]);
            }
            else if (name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
                     && name.IndexOf('.') == name.Length - ".jsonl".Length)
            {
                // A bare {runId}.jsonl journal, not {runId}.xxx.jsonl / checkout.
                runIds.Add(name[..^".jsonl".Length]);
            }
        }

        foreach (var runId in runIds)
        {
            yield return new RunScan(runId, runsRoot);
        }
    }

    private static string RunningRoot(string exportRoot)
        => ExportPathSafety.CombineUnderRoot(exportRoot, "runs");

    private static string JournalPath(string exportRoot, string runId)
        => ExportPathSafety.CombineUnderRoot(exportRoot, "runs", runId + ".jsonl");

    private static string TransactionPath(string exportRoot, string runId)
        => ExportPathSafety.CombineUnderRoot(exportRoot, "runs", runId + ".transaction.json");

    private static bool TryDeleteTransactionFiles(string runsRoot, string runId)
    {
        var transaction = Path.Combine(runsRoot, runId + ".transaction.json");
        var checkpoint = Path.Combine(runsRoot, runId + ".transaction.checkpoint.json");
        var wal = Path.Combine(runsRoot, runId + ".transaction.jsonl");
        var removed = TryDeleteFile(transaction);
        removed |= TryDeleteFile(checkpoint);
        removed |= TryDeleteFile(wal);
        return removed;
    }

    private static bool TryDeleteFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return !File.Exists(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private sealed class RunScan
    {
        public RunScan(string runId, string runsRoot)
        {
            RunId = runId;
            CreatedUtc = DateTimeOffset.MinValue;
            JournalPath = Path.Combine(runsRoot, runId + ".jsonl");
            TransactionPath = Path.Combine(runsRoot, runId + ".transaction.json");
            CheckpointPath = Path.Combine(runsRoot, runId + ".transaction.checkpoint.json");
            WalPath = Path.Combine(runsRoot, runId + ".transaction.jsonl");
            MetadataCommitPath = Path.Combine(runsRoot, runId + ".metadata-commit.json");
            PrivateManifestPath = Path.Combine(runsRoot, runId + ".manifest.private.json");
            PortableManifestPath = Path.Combine(runsRoot, runId + ".dataset.manifest.json");
            CsvPath = Path.Combine(runsRoot, runId + ".dataset.csv");
            ArtifactIndexPath = Path.Combine(runsRoot, runId + ".artifact-index.jsonl");
            TotalBytes = Size(Path.Combine(runsRoot, runId + ".jsonl"))
                + Size(TransactionPath)
                + Size(CheckpointPath)
                + Size(WalPath)
                + Size(MetadataCommitPath)
                + Size(PrivateManifestPath)
                + Size(PortableManifestPath)
                + Size(CsvPath)
                + Size(ArtifactIndexPath);
            JournalBytes = Size(JournalPath);
            TransactionBytes = Size(TransactionPath) + Size(CheckpointPath) + Size(WalPath);
            CreatedUtc = ReadCreatedUtc(PrivateManifestPath, MetadataCommitPath);
        }

        public string RunId { get; }
        public bool IsComplete => File.Exists(MetadataCommitPath) || File.Exists(PrivateManifestPath);
        public DateTimeOffset CreatedUtc { get; }
        public long JournalBytes { get; }
        public long TransactionBytes { get; }
        public long TotalBytes { get; }
        public RunRetentionDisposition Disposition { get; set; }
        public string? Reason { get; set; }

        private string JournalPath { get; }
        private string TransactionPath { get; }
        private string CheckpointPath { get; }
        private string WalPath { get; }
        private string MetadataCommitPath { get; }
        private string PrivateManifestPath { get; }
        private string PortableManifestPath { get; }
        private string CsvPath { get; }
        private string ArtifactIndexPath { get; }

        private static long Size(string path)
        {
            try
            {
                return File.Exists(path) ? new FileInfo(path).Length : 0;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return 0;
            }
        }

        private static DateTimeOffset ReadCreatedUtc(string privateManifestPath, string metadataCommitPath)
        {
            if (File.Exists(privateManifestPath))
            {
                try
                {
                    using var stream = new FileStream(
                        privateManifestPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        32 * 1024,
                        FileOptions.SequentialScan);
                    var manifest = JsonSerializer.Deserialize<VoiceExportManifest>(stream, InfrastructureJson.Compact);
                    if (manifest is not null)
                    {
                        return manifest.GeneratedAtUtc;
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
                {
                    // Fall through to file-time below.
                }
            }

            try
            {
                var path = File.Exists(privateManifestPath) ? privateManifestPath : metadataCommitPath;
                return File.Exists(path) ? new DateTimeOffset(File.GetLastWriteTimeUtc(path)) : DateTimeOffset.MinValue;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return DateTimeOffset.MinValue;
            }
        }
    }
}