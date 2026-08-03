using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WeChatVoice.Core.Models;
using WeChatVoice.Infrastructure.Serialization;

namespace WeChatVoice.Infrastructure.Export;

/// <summary>
/// Verifies a committed export without modifying its source SILK artifacts.
/// Repair reconstructs only derived manifests, CSV files, and the artifact
/// index after the immutable SILK set has passed the same checks.
/// </summary>
public sealed class ExportVerificationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static readonly string[] CsvHeader =
    [
        "item_id",
        "relative_audio_path",
        "sha256",
        "duration_ms",
        "byte_length",
        "quality_flags",
        "training_eligibility",
        "selected",
    ];

    public async Task<ExportVerificationResult> VerifyAsync(
        string exportDirectory,
        string? runId,
        CancellationToken cancellationToken)
    {
        var exportRoot = Path.GetFullPath(exportDirectory);
        var issues = new List<ExportVerificationIssue>();
        if (!Directory.Exists(exportRoot))
        {
            issues.Add(Issue("export-missing", null, "The export directory does not exist."));
            return Result(exportRoot, runId, null, issues, 0, false, false, false);
        }

        var selectedRunId = runId;
        var manifestPath = ResolveManifestPath(exportRoot, runId);
        VoiceExportManifest? manifest = null;
        string? manifestHash = null;
        if (manifestPath is null || !File.Exists(manifestPath))
        {
            issues.Add(Issue("manifest-missing", null, "The requested export manifest does not exist."));
        }
        else
        {
            try
            {
                manifestHash = await FileHashing.ComputeSha256Async(manifestPath, cancellationToken).ConfigureAwait(false);
                manifest = await ReadManifestAsync(manifestPath, cancellationToken).ConfigureAwait(false);
                selectedRunId ??= manifest.RunId;
            }
            catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
            {
                issues.Add(Issue("manifest-invalid", RelativePath(exportRoot, manifestPath), "The export manifest cannot be read or parsed."));
            }
        }

        var journalCommitted = false;
        var csvConsistent = false;
        var trainingSelectionConsistent = false;
        var verifiedOriginalCount = 0;
        var missingFileCount = 0;
        var extraFileCount = 0;
        if (manifest is not null)
        {
            var journalPath = ResolveJournalPath(exportRoot, manifest.RunId)!;
            var journal = await ReadJournalAsync(journalPath, cancellationToken, issues).ConfigureAwait(false);
            var commit = journal.Events.LastOrDefault(static item => item.Event == "manifest-committed");
            journalCommitted = commit is not null;
            if (commit is null)
            {
                issues.Add(Issue("journal-not-committed", RelativePath(exportRoot, journalPath), "The Journal has no flushed manifest-committed event."));
            }
            else if (!string.Equals(commit.ManifestSha256, manifestHash, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(Issue("manifest-hash-mismatch", RelativePath(exportRoot, manifestPath!), "The Journal commit hash does not match the manifest bytes."));
            }

            if (journalPath is not null && File.Exists(journalPath))
            {
                try
                {
                    var reconstructed = await FileSystemVoiceExportStore.ReadManifestFromJournalAsync(
                        new VoiceExportManifest(DateTimeOffset.UtcNow, RunId: manifest.RunId, RunStatus: ExportRunStatus.Failed),
                        journalPath,
                        cancellationToken).ConfigureAwait(false);
                    if (!CanonicalEquals(reconstructed, manifest))
                    {
                        issues.Add(Issue("journal-manifest-mismatch", RelativePath(exportRoot, journalPath), "The committed Journal entries do not reconstruct the manifest."));
                    }
                }
                catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
                {
                    issues.Add(Issue("journal-invalid", RelativePath(exportRoot, journalPath), "The Journal cannot be reconstructed."));
                }
            }

            await AddManifestConsistencyIssuesAsync(exportRoot, manifest, selectedRunId, runId is null, issues, cancellationToken).ConfigureAwait(false);
            var artifactResult = await VerifyArtifactsAsync(exportRoot, manifest, issues, cancellationToken).ConfigureAwait(false);
            verifiedOriginalCount = artifactResult.VerifiedOriginalCount;
            missingFileCount = artifactResult.MissingFileCount;
            extraFileCount = artifactResult.ExtraFileCount;
            var csvResult = await VerifyCsvAsync(exportRoot, manifest, selectedRunId, runId is null, issues, cancellationToken).ConfigureAwait(false);
            csvConsistent = csvResult.IsConsistent;
            trainingSelectionConsistent = csvResult.TrainingSelectionConsistent;
            await VerifyArtifactIndexAsync(exportRoot, artifactResult.ExpectedArtifacts, issues, cancellationToken).ConfigureAwait(false);
        }

        return Result(
            exportRoot,
            selectedRunId,
            manifestHash,
            issues,
            verifiedOriginalCount,
            journalCommitted,
            csvConsistent,
            trainingSelectionConsistent,
            missingFileCount,
            extraFileCount);
    }

    public async Task<ExportRepairResult> RepairAsync(
        string exportDirectory,
        string? runId,
        CancellationToken cancellationToken)
    {
        var exportRoot = Path.GetFullPath(exportDirectory);
        var journalPath = await ResolveRepairJournalPathAsync(exportRoot, runId, cancellationToken).ConfigureAwait(false);
        if (journalPath is null)
        {
            throw new InvalidDataException("No export Journal with a committed manifest was found.");
        }

        var journalRunId = Path.GetFileNameWithoutExtension(journalPath);
        var manifest = await FileSystemVoiceExportStore.ReadManifestFromJournalAsync(
            new VoiceExportManifest(DateTimeOffset.UtcNow, RunId: journalRunId, RunStatus: ExportRunStatus.Failed),
            journalPath,
            cancellationToken).ConfigureAwait(false);
        var issues = new List<ExportVerificationIssue>();
        var journal = await ReadJournalAsync(journalPath, cancellationToken, issues).ConfigureAwait(false);
        var commit = journal.Events.LastOrDefault(static item => item.Event == "manifest-committed");
        if (commit is null || issues.Count > 0)
        {
            throw new InvalidDataException("The export Journal was not committed.");
        }

        var artifactResult = await VerifyArtifactsAsync(exportRoot, manifest, issues, cancellationToken).ConfigureAwait(false);
        if (issues.Count > 0 || artifactResult.MissingFileCount > 0 || artifactResult.ExtraFileCount > 0)
        {
            throw new InvalidDataException("Export repair refused to proceed because the immutable SILK artifact set is not valid.");
        }

        var store = new FileSystemVoiceExportStore(exportRoot);
        await store.RecoverRunAsync(journalPath, cancellationToken).ConfigureAwait(false);
        var verification = await VerifyAsync(exportRoot, manifest.RunId, cancellationToken).ConfigureAwait(false);
        return new ExportRepairResult(verification, journalPath, OriginalArtifactsChanged: false);
    }

    private static async Task<VoiceExportManifest> ReadManifestAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<VoiceExportManifest>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The export manifest is empty.");
    }

    private static async Task<JournalReadResult> ReadJournalAsync(
        string? path,
        CancellationToken cancellationToken,
        ICollection<ExportVerificationIssue> issues)
    {
        if (path is null || !File.Exists(path))
        {
            return new JournalReadResult([]);
        }

        var events = new List<VoiceExportJournalEvent>();
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var item = JsonSerializer.Deserialize<VoiceExportJournalEvent>(line, JsonOptions)
                    ?? throw new InvalidDataException("The Journal contains a null event.");
                events.Add(item);
            }
            catch (JsonException)
            {
                issues.Add(Issue("journal-invalid", RelativePath(Path.GetDirectoryName(path)!, path), "The Journal contains malformed JSON."));
            }
        }

        return new JournalReadResult(events);
    }

    private static async Task<ArtifactVerificationResult> VerifyArtifactsAsync(
        string exportRoot,
        VoiceExportManifest manifest,
        ICollection<ExportVerificationIssue> issues,
        CancellationToken cancellationToken)
    {
        var expected = new Dictionary<string, ArtifactExpectation>(StringComparer.OrdinalIgnoreCase);
        var verifiedOriginal = 0;
        var missing = 0;
        foreach (var entry in manifest.Entries)
        {
            if (!TryResolveArtifactPath(exportRoot, entry.OriginalPath, out var path))
            {
                issues.Add(Issue("artifact-path-outside-root", entry.OriginalPath, "An artifact path is not a safe relative export path."));
                missing++;
                continue;
            }

            var originalPath = NormalizeRelativePath(entry.OriginalPath);
            if (!expected.TryAdd(originalPath, new ArtifactExpectation(entry.OriginalByteLength, entry.OriginalSha256)))
            {
                issues.Add(Issue("artifact-duplicate", originalPath, "The manifest references the same artifact more than once."));
                missing++;
                continue;
            }

            if (!await VerifyArtifactAsync(path, originalPath, entry.OriginalByteLength, entry.OriginalSha256, issues, cancellationToken).ConfigureAwait(false))
            {
                missing++;
            }
            else
            {
                verifiedOriginal++;
            }

            if (entry.DecodedPath is not null)
            {
                if (!TryResolveArtifactPath(exportRoot, entry.DecodedPath, out var decodedPath))
                {
                    issues.Add(Issue("artifact-path-outside-root", entry.DecodedPath, "A decoded artifact path is not safe."));
                }
                else
                {
                    var decodedRelativePath = NormalizeRelativePath(entry.DecodedPath);
                    var decodedLength = File.Exists(decodedPath) ? new FileInfo(decodedPath).Length : -1;
                    if (!expected.TryAdd(decodedRelativePath, new ArtifactExpectation(
                            decodedLength,
                            entry.WavSha256 ?? string.Empty)))
                    {
                        issues.Add(Issue("artifact-duplicate", decodedRelativePath, "The manifest references the same decoded artifact more than once."));
                    }

                    if (entry.WavSha256 is not null && !File.Exists(decodedPath))
                    {
                        issues.Add(Issue("decoded-missing", decodedRelativePath, "A manifest-referenced decoded artifact is missing."));
                        missing++;
                    }
                    else if (entry.WavSha256 is not null && File.Exists(decodedPath))
                    {
                        var metadata = await FileHashing.ComputeMetadataAsync(decodedPath, cancellationToken).ConfigureAwait(false);
                        if (!string.Equals(metadata.Sha256, entry.WavSha256, StringComparison.OrdinalIgnoreCase))
                        {
                            issues.Add(Issue("decoded-hash-mismatch", decodedRelativePath, "A decoded artifact hash does not match the manifest."));
                        }
                    }
                }
            }
        }

        foreach (var directoryName in new[] { "original", "decoded" })
        {
            var directory = Path.Combine(exportRoot, directoryName);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var file in EnumerateRegularFiles(directory, issues, exportRoot))
            {
                if (!expected.ContainsKey(NormalizeRelativePath(RelativePath(exportRoot, file))))
                {
                    issues.Add(Issue("artifact-extra", RelativePath(exportRoot, file), "An artifact file is not referenced by the manifest."));
                }
            }
        }

        return new ArtifactVerificationResult(
            verifiedOriginal,
            missing,
            issues.Count(item => item.Code == "artifact-extra"),
            expected);
    }

    private static async Task<bool> VerifyArtifactAsync(
        string path,
        string relativePath,
        long expectedLength,
        string expectedSha256,
        ICollection<ExportVerificationIssue> issues,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            issues.Add(Issue("artifact-missing", relativePath, "A manifest-referenced artifact is missing."));
            return false;
        }

        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                issues.Add(Issue("artifact-reparse-point", relativePath, "Reparse points are not allowed for export artifacts."));
                return false;
            }

            var metadata = await FileHashing.ComputeMetadataAsync(path, cancellationToken).ConfigureAwait(false);
            if (metadata.ByteLength != expectedLength
                || !string.Equals(metadata.Sha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(Issue("artifact-hash-mismatch", relativePath, "An artifact length or SHA-256 does not match the manifest."));
                return false;
            }

            return true;
        }
        catch (IOException)
        {
            issues.Add(Issue("artifact-unreadable", relativePath, "A manifest-referenced artifact could not be read."));
            return false;
        }
    }

    private static async Task<CsvVerificationResult> VerifyCsvAsync(
        string exportRoot,
        VoiceExportManifest manifest,
        string? runId,
        bool latestSelected,
        ICollection<ExportVerificationIssue> issues,
        CancellationToken cancellationToken)
    {
        var paths = new List<string>();
        if (latestSelected)
        {
            paths.Add(Path.Combine(exportRoot, "dataset.csv"));
            paths.Add(Path.Combine(exportRoot, "manifest.csv"));
        }
        if (!string.IsNullOrWhiteSpace(runId))
        {
            paths.Add(Path.Combine(exportRoot, "runs", runId + ".dataset.csv"));
            paths.Add(Path.Combine(exportRoot, "runs", runId + ".manifest.csv"));
        }

        var allConsistent = true;
        var trainingConsistent = true;
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var relativePath = RelativePath(exportRoot, path);
            if (!File.Exists(path))
            {
                issues.Add(Issue("csv-missing", relativePath, "The portable dataset CSV is missing."));
                allConsistent = false;
                trainingConsistent = false;
                continue;
            }

            try
            {
                var rows = await ReadCsvAsync(path, cancellationToken).ConfigureAwait(false);
                if (rows.Count == 0 || !rows[0].SequenceEqual(CsvHeader, StringComparer.Ordinal))
                {
                    issues.Add(Issue("csv-header-invalid", relativePath, "The portable dataset CSV header is invalid."));
                    allConsistent = false;
                    trainingConsistent = false;
                    continue;
                }

                var expected = manifest.Entries.ToDictionary(ExportItemIdentity.ComputeItemId, StringComparer.OrdinalIgnoreCase);
                var actual = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
                foreach (var row in rows.Skip(1))
                {
                    if (row.Length != CsvHeader.Length || !actual.TryAdd(row[0], row.ToArray()))
                    {
                        issues.Add(Issue("csv-row-invalid", relativePath, "The portable dataset CSV contains an invalid or duplicate row."));
                        allConsistent = false;
                        trainingConsistent = false;
                    }
                }

                foreach (var pair in expected)
                {
                    if (!actual.TryGetValue(pair.Key, out var row))
                    {
                        issues.Add(Issue("csv-row-missing", relativePath, "The portable dataset CSV is missing a manifest item."));
                        allConsistent = false;
                        trainingConsistent = false;
                        continue;
                    }

                    var entry = pair.Value;
                    var expectedSelected = entry.UserSelectionState == UserSelectionState.Selected ? "True" : "False";
                    var expectedEligibility = entry.TrainingEligibility.ToString();
                    if (!string.Equals(row[1], entry.OriginalPath, StringComparison.Ordinal)
                        || !string.Equals(row[2], entry.OriginalSha256, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(row[3], entry.DurationMs?.ToString(CultureInfo.InvariantCulture) ?? string.Empty, StringComparison.Ordinal)
                        || !string.Equals(row[4], entry.OriginalByteLength.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                        || !string.Equals(row[5], string.Join('|', entry.QualityFlags), StringComparison.Ordinal)
                        || !string.Equals(row[6], expectedEligibility, StringComparison.Ordinal)
                        || !string.Equals(row[7], expectedSelected, StringComparison.OrdinalIgnoreCase))
                    {
                        issues.Add(Issue("csv-json-mismatch", relativePath, "The portable dataset CSV differs from the JSON manifest."));
                        allConsistent = false;
                        trainingConsistent = false;
                    }
                }

                if (actual.Keys.Except(expected.Keys, StringComparer.OrdinalIgnoreCase).Any())
                {
                    issues.Add(Issue("csv-row-extra", relativePath, "The portable dataset CSV contains an item not in the JSON manifest."));
                    allConsistent = false;
                    trainingConsistent = false;
                }
            }
            catch (IOException)
            {
                issues.Add(Issue("csv-unreadable", relativePath, "The portable dataset CSV could not be read."));
                allConsistent = false;
                trainingConsistent = false;
            }
        }

        return new CsvVerificationResult(allConsistent, trainingConsistent);
    }

    private static async Task VerifyArtifactIndexAsync(
        string exportRoot,
        IReadOnlyDictionary<string, ArtifactExpectation> expected,
        ICollection<ExportVerificationIssue> issues,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(exportRoot, "artifact-index.jsonl");
        if (!File.Exists(path))
        {
            issues.Add(Issue("artifact-index-missing", "artifact-index.jsonl", "The artifact index is missing; run export repair."));
            return;
        }

        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 32 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var indexEntry = JsonSerializer.Deserialize<ArtifactIndexRecord>(line, JsonOptions)
                    ?? throw new InvalidDataException("The artifact index contains a null entry.");
                var relativePath = NormalizeRelativePath(indexEntry.RelativePath);
                if (!TryResolveArtifactPath(exportRoot, relativePath, out _))
                {
                    issues.Add(Issue("artifact-index-path-outside-root", relativePath, "The artifact index contains a path outside the export root."));
                    continue;
                }

                if (!found.Add(relativePath))
                {
                    issues.Add(Issue("artifact-index-duplicate", relativePath, "The artifact index contains a duplicate path."));
                    continue;
                }

                if (!expected.TryGetValue(relativePath, out var expectedEntry)
                    || indexEntry.Length != expectedEntry.Length
                    || !string.Equals(indexEntry.Sha256, expectedEntry.Sha256, StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(indexEntry.FileId))
                {
                    issues.Add(Issue("artifact-index-mismatch", relativePath, "The artifact index length or SHA-256 does not match the manifest."));
                }
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException)
            {
                issues.Add(Issue("artifact-index-invalid", "artifact-index.jsonl", "The artifact index contains malformed JSON."));
            }
        }

        if (!found.SetEquals(expected.Keys))
        {
            issues.Add(Issue("artifact-index-mismatch", "artifact-index.jsonl", "The artifact index does not cover exactly the manifest artifacts."));
        }
    }

    private static async Task<IReadOnlyList<string[]>> ReadCsvAsync(string path, CancellationToken cancellationToken)
    {
        var text = await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        var rows = new List<string[]>();
        var row = new List<string>();
        var value = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (quoted)
            {
                if (character == '"' && index + 1 < text.Length && text[index + 1] == '"')
                {
                    value.Append('"');
                    index++;
                }
                else if (character == '"')
                {
                    quoted = false;
                }
                else
                {
                    value.Append(character);
                }
            }
            else if (character == '"' && value.Length == 0)
            {
                quoted = true;
            }
            else if (character == ',')
            {
                row.Add(value.ToString());
                value.Clear();
            }
            else if (character is '\r' or '\n')
            {
                if (character == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
                row.Add(value.ToString());
                value.Clear();
                if (row.Count > 1 || row[0].Length > 0) rows.Add(row.ToArray());
                row.Clear();
            }
            else
            {
                value.Append(character);
            }
        }

        if (quoted) throw new InvalidDataException("The CSV contains an unterminated quoted field.");
        if (value.Length > 0 || row.Count > 0)
        {
            row.Add(value.ToString());
            rows.Add(row.ToArray());
        }

        return rows;
    }

    private static async Task AddManifestConsistencyIssuesAsync(
        string exportRoot,
        VoiceExportManifest manifest,
        string? runId,
        bool latestSelected,
        ICollection<ExportVerificationIssue> issues,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var paths = new List<string>
        {
            Path.Combine(exportRoot, "runs", (runId ?? manifest.RunId) + ".manifest.private.json"),
            Path.Combine(exportRoot, "runs", (runId ?? manifest.RunId) + ".manifest.json"),
        };
        if (latestSelected)
        {
            paths.Add(Path.Combine(exportRoot, "manifest.private.json"));
            paths.Add(Path.Combine(exportRoot, "latest.manifest.json"));
        }
        foreach (var path in paths)
        {
            if (!File.Exists(path))
            {
                issues.Add(Issue("manifest-alias-missing", RelativePath(exportRoot, path), "A committed manifest alias is missing."));
                continue;
            }

            try
            {
                var alias = await ReadManifestAsync(path, cancellationToken).ConfigureAwait(false);
                if (!CanonicalEquals(alias, manifest))
                {
                    issues.Add(Issue("manifest-alias-mismatch", RelativePath(exportRoot, path), "A committed manifest alias differs from the selected manifest."));
                }
            }
            catch (Exception)
            {
                issues.Add(Issue("manifest-alias-invalid", RelativePath(exportRoot, path), "A committed manifest alias cannot be parsed."));
            }
        }
    }

    private static async Task<string?> ResolveRepairJournalPathAsync(string exportRoot, string? runId, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(runId))
        {
            var path = ResolveJournalPath(exportRoot, runId);
            return path is not null && File.Exists(path) ? path : null;
        }

        var runs = Path.Combine(exportRoot, "runs");
        if (!Directory.Exists(runs)) return null;
        foreach (var path in Directory.EnumerateFiles(runs, "*.jsonl").OrderByDescending(File.GetLastWriteTimeUtc))
        {
            var events = await ReadJournalAsync(path, cancellationToken, new List<ExportVerificationIssue>()).ConfigureAwait(false);
            if (events.Events.Any(static item => item.Event == "manifest-committed")) return path;
        }

        return null;
    }

    private static string? ResolveManifestPath(string exportRoot, string? runId)
        => string.IsNullOrWhiteSpace(runId)
            ? Path.Combine(exportRoot, "latest.manifest.json")
            : Path.Combine(exportRoot, "runs", runId + ".manifest.json");

    private static string? ResolveJournalPath(string exportRoot, string? runId)
        => string.IsNullOrWhiteSpace(runId) ? null : Path.Combine(exportRoot, "runs", runId + ".jsonl");

    private static IEnumerable<string> EnumerateRegularFiles(
        string root,
        ICollection<ExportVerificationIssue> issues,
        string exportRoot)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    issues.Add(Issue("artifact-reparse-point", RelativePath(exportRoot, entry), "Reparse points are not allowed in export artifacts."));
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                else yield return Path.GetFullPath(entry);
            }
        }
    }

    private static bool TryResolveArtifactPath(string exportRoot, string relativePath, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath)) return false;
        try
        {
            path = ExportPathSafety.CombineUnderRoot(exportRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            return path.StartsWith(exportRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool CanonicalEquals(VoiceExportManifest left, VoiceExportManifest right)
        => JsonSerializer.SerializeToUtf8Bytes(left, JsonOptions).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(right, JsonOptions));

    private static string NormalizeRelativePath(string path)
        => path.Replace('\\', '/');

    private static string RelativePath(string root, string path)
        => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    private static ExportVerificationIssue Issue(string code, string? relativePath, string detail)
        => new(code, relativePath, detail);

    private static ExportVerificationResult Result(
        string exportRoot,
        string? runId,
        string? manifestHash,
        IReadOnlyCollection<ExportVerificationIssue> issues,
        int verifiedOriginalCount,
        bool journalCommitted,
        bool csvConsistent,
        bool trainingSelectionConsistent,
        int missingFileCount = 0,
        int extraFileCount = 0)
        => new(
            exportRoot,
            runId,
            issues.Count == 0,
            manifestHash,
            verifiedOriginalCount,
            missingFileCount,
            extraFileCount,
            journalCommitted,
            csvConsistent,
            trainingSelectionConsistent,
            issues.ToArray());

    private sealed record JournalReadResult(IReadOnlyList<VoiceExportJournalEvent> Events);

    private sealed record ArtifactExpectation(long Length, string Sha256);

    private sealed record ArtifactIndexRecord(
        string RelativePath,
        string? FileId,
        long Length,
        long LastWriteUtcTicks,
        string Sha256,
        DateTimeOffset LastVerifiedUtc);

    private sealed record ArtifactVerificationResult(
        int VerifiedOriginalCount,
        int MissingFileCount,
        int ExtraFileCount,
        IReadOnlyDictionary<string, ArtifactExpectation> ExpectedArtifacts);

    private sealed record CsvVerificationResult(bool IsConsistent, bool TrainingSelectionConsistent);
}
