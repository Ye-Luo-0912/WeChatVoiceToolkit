using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WeChatVoice.Core.Models;
using WeChatVoice.Core.Ports;
using WeChatVoice.Infrastructure.Serialization;

namespace WeChatVoice.Infrastructure.Export;

/// <summary>
/// A local export store that owns stable source-key paths, independently
/// verifies original/decoded artifacts, and commits replacements atomically.
/// </summary>
public sealed class FileSystemVoiceExportStore : IVoiceExportStore
{
    private readonly ConcurrentDictionary<string, byte> _reservedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _exportRoot;
    private readonly IExportTransactionFaultInjector? _faultInjector;
    private readonly SemaphoreSlim _artifactIndexGate = new(1, 1);
    private readonly SemaphoreSlim _namespaceKeyGate = new(1, 1);
    private Dictionary<string, ArtifactIndexEntry>? _artifactIndex;

    public FileSystemVoiceExportStore(
        string exportRoot,
        IExportTransactionFaultInjector? faultInjector = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exportRoot);
        _exportRoot = Path.GetFullPath(exportRoot);
        _faultInjector = faultInjector;
    }

    public string ExportRoot => _exportRoot;

    internal void ThrowIfFaultRequested(
        ExportTransactionFaultPoint point,
        string runId,
        string? messageId = null)
        => _faultInjector?.ThrowIfRequested(point, runId, messageId);

    public async ValueTask<IExportRunLease> BeginRunAsync(
        VoiceExportRunContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        var runsDirectory = ExportPathSafety.CombineUnderRoot(_exportRoot, "runs");
        Directory.CreateDirectory(runsDirectory);
        var operationId = Guid.NewGuid().ToString("N");
        var rootLock = await ExportRootLock.AcquireAsync(
            _exportRoot,
            ExportRootLockMode.Exclusive,
            operationId,
            context.RunId,
            cancellationToken).ConfigureAwait(false);
        try
        {
            await RecoverPendingTransactionsUnderLockAsync(cancellationToken, rootLock).ConfigureAwait(false);
            var journalPath = ExportPathSafety.CombineUnderRoot(_exportRoot, "runs", context.RunId + ".jsonl");
            var stream = new FileStream(
                journalPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var journal = new FileSystemExportRunJournal(this, _exportRoot, context, operationId, stream, rootLock);
            await journal.InitializeAsync(cancellationToken).ConfigureAwait(false);
            return journal;
        }
        catch
        {
            await rootLock.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public ValueTask<IExportItemLease> BeginItemAsync(
        VoiceRecord record,
        ExistingArtifactPolicy policy,
        CancellationToken cancellationToken)
        => BeginItemCoreAsync(record, policy, _exportRoot, deferPublish: false, cancellationToken);

    private async ValueTask<IExportItemLease> BeginItemCoreAsync(
        VoiceRecord record,
        ExistingArtifactPolicy policy,
        string artifactRoot,
        bool deferPublish,
        CancellationToken cancellationToken,
        Func<FileSystemExportItemLease, CancellationToken, Task>? changed = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();
        var sourceStableKey = record.SourceStableKey
            ?? throw new SourceIdentityRequiredException("The voice record lacks a complete SourceStableKey; reusable export is refused.");

        var stableKeyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceStableKey)))
            .ToLowerInvariant();
        var prefix1 = stableKeyHash[..2];
        var prefix2 = stableKeyHash[2..4];
        var fileName = stableKeyHash[..32];
        var originalManifestPath = $"original/{prefix1}/{prefix2}/{fileName}.silk";
        var decodedManifestPath = $"decoded/{prefix1}/{prefix2}/{fileName}.wav";
        var originalPath = ExportPathSafety.CombineUnderRoot(artifactRoot, "original", prefix1, prefix2, $"{fileName}.silk");
        var decodedPath = ExportPathSafety.CombineUnderRoot(artifactRoot, "decoded", prefix1, prefix2, $"{fileName}.wav");
        var finalOriginalPath = ExportPathSafety.CombineUnderRoot(_exportRoot, "original", prefix1, prefix2, $"{fileName}.silk");
        var finalDecodedPath = ExportPathSafety.CombineUnderRoot(_exportRoot, "decoded", prefix1, prefix2, $"{fileName}.wav");

        var original = await ReadExistingArtifactAsync(finalOriginalPath, originalManifestPath, cancellationToken).ConfigureAwait(false);
        var decoded = await ReadExistingArtifactAsync(finalDecodedPath, decodedManifestPath, cancellationToken).ConfigureAwait(false);
        var originalState = GetOriginalState(original, record);
        var decodedState = await GetDecodedStateAsync(decoded, record, finalDecodedPath, cancellationToken).ConfigureAwait(false);

        if (policy == ExistingArtifactPolicy.Fail
            && (originalState != ExportArtifactState.Missing || decodedState != ExportArtifactState.Missing))
        {
            throw new ExistingArtifactConflictException($"An original artifact already exists for source key '{sourceStableKey}'.");
        }

        if (policy == ExistingArtifactPolicy.VerifyOnly
            && originalState == ExportArtifactState.PendingExisting)
        {
            throw new ExistingArtifactNeedsHashException($"Existing artifact '{originalManifestPath}' cannot be verified because the source payload hash is unknown.");
        }

        var replace = policy == ExistingArtifactPolicy.Replace;
        var reserveOriginal = replace || originalState is ExportArtifactState.Missing or ExportArtifactState.PendingExisting;
        var reserveDecoded = replace || decodedState == ExportArtifactState.Missing;
        var reserved = new List<string>(2);
        try
        {
            if (reserveOriginal)
            {
                Reserve(finalOriginalPath, sourceStableKey);
                reserved.Add(finalOriginalPath);
            }

            if (reserveDecoded)
            {
                Reserve(finalDecodedPath, sourceStableKey);
                reserved.Add(finalDecodedPath);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(originalPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(decodedPath)!);
            return new FileSystemExportItemLease(
                this,
                record,
                originalManifestPath,
                decodedManifestPath,
                originalPath,
                decodedPath,
                finalOriginalPath,
                finalDecodedPath,
                originalState,
                decodedState,
                original,
                decoded,
                replace,
                deferPublish,
                () => Release(reserved),
                changed);
        }
        catch
        {
            Release(reserved);
            throw;
        }
    }

    public async Task<VoiceExportManifest> RecoverRunAsync(string journalPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journalPath);
        var fullJournalPath = Path.GetFullPath(journalPath);
        var runsDirectory = ExportPathSafety.CombineUnderRoot(_exportRoot, "runs");
        var expectedPrefix = runsDirectory.EndsWith(Path.DirectorySeparatorChar) ? runsDirectory : runsDirectory + Path.DirectorySeparatorChar;
        if (!fullJournalPath.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetDirectoryName(fullJournalPath), runsDirectory, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetExtension(fullJournalPath), ".jsonl", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The recovery journal must be a .jsonl file directly under the export root's runs directory.");
        }

        var runId = Path.GetFileNameWithoutExtension(fullJournalPath);
        await using var rootLock = await ExportRootLock.AcquireAsync(
            _exportRoot,
            ExportRootLockMode.Exclusive,
            Guid.NewGuid().ToString("N"),
            runId,
            cancellationToken).ConfigureAwait(false);
        // The explicit recovery command must use the same strict
        // transaction reconciliation as a new export/verify operation. It
        // must never rebuild metadata merely because a Journal mentions an
        // artifact whose final bytes are missing or altered.
        await RecoverPendingTransactionsUnderLockAsync(
            cancellationToken,
            rootLock,
            onlyRunId: runId,
            resumeMetadata: false).ConfigureAwait(false);
        return await RecoverRunUnderLockAsync(fullJournalPath, runId, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<VoiceExportManifest> RecoverRunUnderLockAsync(
        string fullJournalPath,
        string runId,
        CancellationToken cancellationToken)
    {
        var transactionPath = ExportPathSafety.CombineUnderRoot(_exportRoot, "runs", runId + ".transaction.json");
        var hasTransactionDocument = File.Exists(transactionPath);
        var hasRecordedTransactionItems = false;
        if (hasTransactionDocument)
        {
            try
            {
                var transaction = await ReadJsonDocumentAsync<ExportTransactionDocument>(transactionPath, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(transaction.RunId, runId, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("The export transaction RunId does not match the recovery Journal.");
                }

                if (transaction.State == ExportTransactionState.RolledBack
                    || string.Equals(transaction.FailureCode, "export-rolled-back", StringComparison.Ordinal))
                {
                    throw new InvalidDataException("The export transaction was explicitly rolled back and cannot be recovered.");
                }

                hasRecordedTransactionItems = transaction.Items.Count > 0;
                await AppendRecoveredItemEventsAsync(transaction, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
            {
                throw new IOException("The export transaction document could not be read during recovery.", exception);
            }
        }

        var fallback = new VoiceExportManifest(DateTimeOffset.UtcNow, RunId: runId, RunStatus: ExportRunStatus.Failed);
        var recovered = await ReadManifestFromJournalAsync(fallback, fullJournalPath, cancellationToken).ConfigureAwait(false);
        var completedTransaction = hasTransactionDocument
            ? await ReadJsonDocumentAsync<ExportTransactionDocument>(transactionPath, cancellationToken).ConfigureAwait(false)
            : null;
        if (await JournalHasManifestCommitAsync(fullJournalPath, cancellationToken).ConfigureAwait(false)
            && await MetadataCommitIsCompleteAsync(
                runId,
                cancellationToken,
                requireLatestAliases: completedTransaction?.State is not ExportTransactionState.Completed).ConfigureAwait(false))
        {
            if (completedTransaction?.State is not ExportTransactionState.Completed)
            {
                await MarkTransactionCompletedAsync(transactionPath, runId, cancellationToken).ConfigureAwait(false);
            }

            return recovered;
        }

        var existingPrivatePath = ExportPathSafety.CombineUnderRoot(_exportRoot, "runs", runId + ".manifest.private.json");
        if (File.Exists(existingPrivatePath))
        {
            try
            {
                var existingManifest = await ReadJsonDocumentAsync<VoiceExportManifest>(existingPrivatePath, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(existingManifest.DatasetNamespaceKey))
                {
                    recovered = recovered with { DatasetNamespaceKey = existingManifest.DatasetNamespaceKey };
                }
            }
            catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
            {
                // The journal remains authoritative; CommitMetadataAsync will
                // regenerate the private manifest with a run-scoped namespace.
            }
        }

        // A transaction document is the strict current-format boundary: its
        // item hashes must all be present before metadata can be committed.
        // Only old Journals, or a transaction document with no recorded item
        // hashes, use the compatibility path. Every recorded transaction item
        // remains strict and must be reconciled before metadata is committed.
        var metadata = await CommitMetadataAsync(
            recovered,
            runId,
            cancellationToken,
            allowMissingArtifacts: !hasRecordedTransactionItems).ConfigureAwait(false);
        recovered = metadata.Manifest;
        var descriptor = metadata.Descriptor;
        var commitEvent = new VoiceExportJournalEvent(
            "manifest-committed",
            runId,
            DateTimeOffset.UtcNow,
            ManifestSha256: descriptor.PrivateManifestSha256,
            ManifestGeneratedAtUtc: recovered.GeneratedAtUtc);
        await AppendJournalEventDurablyAsync(fullJournalPath, commitEvent, cancellationToken).ConfigureAwait(false);
        if (File.Exists(transactionPath))
        {
            try
            {
                var transaction = await ReadJsonDocumentAsync<ExportTransactionDocument>(transactionPath, cancellationToken).ConfigureAwait(false);
                await AtomicFileWriter.WriteJsonAsync(
                    transactionPath,
                    new ExportTransactionDocument(
                        transaction.RunId,
                        transaction.OperationId,
                        transaction.SelectionFingerprint,
                        ExportTransactionState.Completed,
                        DateTimeOffset.UtcNow,
                        transaction.Items,
                        descriptor),
                    InfrastructureJson.Indented,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
            {
                // The manifests and flushed Journal are still recoverable even
                // when the final transaction marker cannot be refreshed.
            }
        }
        return recovered;
    }

    internal async Task<string> RebuildArtifactIndexAsync(
        VoiceExportManifest manifest,
        CancellationToken cancellationToken,
        bool allowMissingArtifacts = false,
        string? destinationPath = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var entries = new List<ArtifactIndexEntry>();
        foreach (var item in manifest.Entries)
        {
            AddIndexEntry(item.OriginalPath, item.OriginalSha256);
            if (item.DecodedPath is not null && item.WavSha256 is not null)
            {
                AddIndexEntry(item.DecodedPath, item.WavSha256);
            }
        }

        var indexPath = destinationPath is null
            ? Path.Combine(_exportRoot, "artifact-index.jsonl")
            : ExportPathSafety.CombineUnderRoot(_exportRoot, destinationPath);
        var text = string.Join(
            Environment.NewLine,
            entries.Select(static entry => JsonSerializer.Serialize(entry, InfrastructureJson.Compact)))
            + (entries.Count == 0 ? string.Empty : Environment.NewLine);
        await AtomicFileWriter.WriteTextAsync(indexPath, text, cancellationToken).ConfigureAwait(false);
        return await FileHashing.ComputeSha256Async(indexPath, cancellationToken).ConfigureAwait(false);

        void AddIndexEntry(string relativePath, string sha256)
        {
            var fullPath = ExportPathSafety.CombineUnderRoot(_exportRoot, relativePath);
            if (!File.Exists(fullPath))
            {
                if (allowMissingArtifacts)
                {
                    return;
                }

                throw new InvalidDataException("The artifact index cannot reference a missing export artifact.");
            }

            var info = new FileInfo(fullPath);
            entries.Add(new ArtifactIndexEntry(
                relativePath.Replace('\\', '/'),
                ArtifactFileIdentity.Read(fullPath),
                info.Length,
                info.LastWriteTimeUtc.Ticks,
                sha256,
                DateTimeOffset.UtcNow));
        }
    }

    /// <summary>
    /// Re-publishes the latest metadata aliases from an already committed run.
    /// This is intentionally an explicit repair operation: startup recovery
    /// must not replay an older completed run merely because a latest alias is
    /// missing after a crash or manual cleanup.
    /// </summary>
    internal async Task RepairLatestAliasesUnderLockAsync(
        string runId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        var runsRoot = ExportPathSafety.CombineUnderRoot(_exportRoot, "runs");
        var descriptorPath = ExportPathSafety.CombineUnderRoot(runsRoot, runId + ".metadata-commit.json");
        var descriptor = await ReadJsonDocumentAsync<ExportMetadataCommitDescriptor>(descriptorPath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(descriptor.RunId, runId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The metadata commit descriptor RunId does not match the requested repair run.");
        }

        var files = new[]
        {
            (RunPath: ExportPathSafety.CombineUnderRoot(runsRoot, ExportManifestLayout.RunPrivateManifestFileName(runId)),
                LatestPath: ExportPathSafety.CombineUnderRoot(_exportRoot, ExportManifestLayout.PrivateManifestFileName),
                ExpectedHash: descriptor.PrivateManifestSha256),
            (RunPath: ExportPathSafety.CombineUnderRoot(runsRoot, ExportManifestLayout.RunPortableManifestFileName(runId)),
                LatestPath: ExportPathSafety.CombineUnderRoot(_exportRoot, ExportManifestLayout.PortableManifestFileName),
                ExpectedHash: descriptor.PortableManifestSha256),
            (RunPath: ExportPathSafety.CombineUnderRoot(runsRoot, ExportManifestLayout.RunPortableCsvFileName(runId)),
                LatestPath: ExportPathSafety.CombineUnderRoot(_exportRoot, ExportManifestLayout.PortableCsvFileName),
                ExpectedHash: descriptor.DatasetCsvSha256),
            (RunPath: ExportPathSafety.CombineUnderRoot(runsRoot, ExportManifestLayout.RunArtifactIndexFileName(runId)),
                LatestPath: ExportPathSafety.CombineUnderRoot(_exportRoot, "artifact-index.jsonl"),
                ExpectedHash: descriptor.ArtifactIndexSha256),
            (RunPath: descriptorPath,
                LatestPath: ExportPathSafety.CombineUnderRoot(_exportRoot, "latest.metadata-commit.json"),
                ExpectedHash: (string?)null),
        };

        foreach (var file in files)
        {
            if (!File.Exists(file.RunPath))
            {
                throw new InvalidDataException($"The committed metadata file '{file.RunPath}' is missing.");
            }

            if (file.ExpectedHash is not null)
            {
                var actualHash = await FileHashing.ComputeSha256Async(file.RunPath, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(actualHash, file.ExpectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"The committed metadata file '{file.RunPath}' failed its descriptor hash check.");
                }
            }

            await AtomicFileWriter.WriteStreamAsync(
                file.LatestPath,
                async (destination, token) =>
                {
                    await using var source = new FileStream(
                        file.RunPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        128 * 1024,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await source.CopyToAsync(destination, token).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);
        }
    }

    internal async Task<string> GetOrCreateDatasetNamespaceKeyAsync(CancellationToken cancellationToken)
    {
        await _namespaceKeyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var metadataDirectory = ExportPathSafety.CombineUnderRoot(_exportRoot, ".wechatvoice");
            Directory.CreateDirectory(metadataDirectory);
            var path = ExportPathSafety.CombineUnderRoot(_exportRoot, ".wechatvoice", "dataset-namespace.key");
            if (File.Exists(path))
            {
                var existing = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                if (existing.Trim().Length == 64 && existing.Trim().All(Uri.IsHexDigit))
                {
                    return existing.Trim().ToLowerInvariant();
                }

                throw new InvalidDataException("The export dataset namespace key is invalid.");
            }

            var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            await AtomicFileWriter.WriteTextAsync(path, key + Environment.NewLine, cancellationToken).ConfigureAwait(false);
            return key;
        }
        finally
        {
            _namespaceKeyGate.Release();
        }
    }

    internal async Task<string> GetDatasetNamespaceKeyAsync(
        string runId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        var rootKey = await GetOrCreateDatasetNamespaceKeyAsync(cancellationToken).ConfigureAwait(false);
        var derived = HMACSHA256.HashData(
            Convert.FromHexString(rootKey),
            Encoding.UTF8.GetBytes(runId));
        return Convert.ToHexString(derived).ToLowerInvariant();
    }

    private async Task<(VoiceExportManifest Manifest, ExportMetadataCommitDescriptor Descriptor)> CommitMetadataAsync(
        VoiceExportManifest manifest,
        string runId,
        CancellationToken cancellationToken,
        bool allowMissingArtifacts = false)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (string.IsNullOrWhiteSpace(manifest.DatasetNamespaceKey))
        {
            manifest = manifest with
            {
                DatasetNamespaceKey = await GetDatasetNamespaceKeyAsync(runId, cancellationToken).ConfigureAwait(false),
            };
        }

        var runsRoot = ExportPathSafety.CombineUnderRoot(_exportRoot, "runs");
        var stagingRoot = ExportPathSafety.CombineUnderRoot(_exportRoot, "runs", "." + runId + ".metadata.staging");
        Directory.CreateDirectory(runsRoot);
        Directory.CreateDirectory(stagingRoot);
        var stagedPrivate = Path.Combine(stagingRoot, "manifest.private.json");
        var stagedPortable = Path.Combine(stagingRoot, "dataset.manifest.json");
        var stagedCsv = Path.Combine(stagingRoot, "dataset.csv");
        var stagedIndex = Path.Combine(stagingRoot, "artifact-index.jsonl");
        var stagedDescriptor = Path.Combine(stagingRoot, "metadata-commit.json");

        var portable = ExportItemIdentity.ToPortableManifest(manifest);
        await AtomicFileWriter.WriteJsonAsync(stagedPrivate, manifest, InfrastructureJson.Indented, cancellationToken).ConfigureAwait(false);
        await AtomicFileWriter.WriteJsonAsync(stagedPortable, portable, InfrastructureJson.Indented, cancellationToken).ConfigureAwait(false);
        await VoiceManifestCsvWriter.WriteAsync(stagedCsv, manifest, cancellationToken).ConfigureAwait(false);
        var indexHash = await RebuildArtifactIndexAsync(
            manifest,
            cancellationToken,
            allowMissingArtifacts,
            Path.GetRelativePath(_exportRoot, stagedIndex)).ConfigureAwait(false);
        var descriptor = new ExportMetadataCommitDescriptor(
            runId,
            await FileHashing.ComputeSha256Async(stagedPrivate, cancellationToken).ConfigureAwait(false),
            await FileHashing.ComputeSha256Async(stagedPortable, cancellationToken).ConfigureAwait(false),
            await FileHashing.ComputeSha256Async(stagedCsv, cancellationToken).ConfigureAwait(false),
            indexHash);
        await AtomicFileWriter.WriteJsonAsync(stagedDescriptor, descriptor, InfrastructureJson.Indented, cancellationToken).ConfigureAwait(false);

        var runPrivate = ExportPathSafety.CombineUnderRoot(_exportRoot, "runs", ExportManifestLayout.RunPrivateManifestFileName(runId));
        var runPortable = ExportPathSafety.CombineUnderRoot(_exportRoot, "runs", ExportManifestLayout.RunPortableManifestFileName(runId));
        var runCsv = ExportPathSafety.CombineUnderRoot(_exportRoot, "runs", ExportManifestLayout.RunPortableCsvFileName(runId));
        var runIndex = ExportPathSafety.CombineUnderRoot(_exportRoot, "runs", ExportManifestLayout.RunArtifactIndexFileName(runId));
        var runDescriptor = ExportPathSafety.CombineUnderRoot(_exportRoot, "runs", runId + ".metadata-commit.json");
        var indexPath = ExportPathSafety.CombineUnderRoot(_exportRoot, "artifact-index.jsonl");
        MoveFileReplacing(stagedPrivate, runPrivate);
        MoveFileReplacing(stagedPortable, runPortable);
        MoveFileReplacing(stagedCsv, runCsv);
        MoveFileReplacing(stagedIndex, runIndex);
        // Keep the root index as the latest alias, but bind the descriptor to
        // the immutable per-run snapshot above. This prevents a later export
        // from making an older completed run appear incomplete during startup
        // recovery or historical verification.
        var stagedLatestIndex = Path.Combine(stagingRoot, "artifact-index.latest.jsonl");
        File.Copy(runIndex, stagedLatestIndex, overwrite: true);
        MoveFileReplacing(stagedLatestIndex, indexPath);
        MoveFileReplacing(stagedDescriptor, runDescriptor);

        // The run descriptor is the durable metadata commit boundary. Latest
        // aliases are published only after it exists; recovery can safely
        // repeat this idempotent sequence after a crash at any point here.
        await AtomicFileWriter.WriteJsonAsync(
            ExportPathSafety.CombineUnderRoot(_exportRoot, ExportManifestLayout.PrivateManifestFileName),
            manifest,
            InfrastructureJson.Indented,
            cancellationToken).ConfigureAwait(false);
        await AtomicFileWriter.WriteJsonAsync(
            ExportPathSafety.CombineUnderRoot(_exportRoot, ExportManifestLayout.PortableManifestFileName),
            portable,
            InfrastructureJson.Indented,
            cancellationToken).ConfigureAwait(false);
        await VoiceManifestCsvWriter.WriteAsync(
            ExportPathSafety.CombineUnderRoot(_exportRoot, ExportManifestLayout.PortableCsvFileName),
            manifest,
            cancellationToken).ConfigureAwait(false);
        await AtomicFileWriter.WriteJsonAsync(
            ExportPathSafety.CombineUnderRoot(_exportRoot, "latest.metadata-commit.json"),
            descriptor,
            InfrastructureJson.Indented,
            cancellationToken).ConfigureAwait(false);
        DeleteFileIfExists(Path.Combine(_exportRoot, ExportManifestLayout.LegacyPortableManifestFileName));
        DeleteFileIfExists(Path.Combine(_exportRoot, "manifest.csv"));
        DeleteFileIfExists(Path.Combine(_exportRoot, "runs", runId + ".manifest.json"));
        DeleteFileIfExists(Path.Combine(_exportRoot, "runs", runId + ".manifest.csv"));
        TryDeleteDirectory(stagingRoot);
        return (manifest, descriptor);
    }

    /// <summary>
    /// Reconciles durable export transaction documents before another export,
    /// verify, or repair operation starts. A final file is accepted only after
    /// its recorded length and SHA-256 match; an ambiguous file is left in
    /// place and the transaction remains FailedRecoverable.
    /// </summary>
    public async Task RecoverPendingTransactionsAsync(CancellationToken cancellationToken)
    {
        var operationId = Guid.NewGuid().ToString("N");
        await using var rootLock = await ExportRootLock.AcquireAsync(
            _exportRoot,
            ExportRootLockMode.Exclusive,
            operationId,
            runId: null,
            cancellationToken).ConfigureAwait(false);
        await RecoverPendingTransactionsUnderLockAsync(cancellationToken, rootLock).ConfigureAwait(false);
    }

    internal async Task RecoverPendingTransactionsUnderLockAsync(
        CancellationToken cancellationToken,
        ExportRootLock heldLock,
        string? onlyRunId = null,
        bool resumeMetadata = true)
    {
        _ = heldLock;
        var runsDirectory = ExportPathSafety.CombineUnderRoot(_exportRoot, "runs");
        if (!Directory.Exists(runsDirectory))
        {
            return;
        }

        foreach (var transactionPath in Directory.EnumerateFiles(runsDirectory, "*.transaction.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(onlyRunId)
                && !string.Equals(Path.GetFileNameWithoutExtension(transactionPath).Replace(".transaction", string.Empty, StringComparison.Ordinal), onlyRunId, StringComparison.Ordinal))
            {
                continue;
            }

            ExportTransactionDocument? document;
            try
            {
                await using var stream = new FileStream(transactionPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                document = await JsonSerializer.DeserializeAsync<ExportTransactionDocument>(stream, InfrastructureJson.Compact, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
            {
                throw new InvalidDataException("An export transaction document could not be read safely.", exception);
            }

            if (document is null)
            {
                continue;
            }

            if (document.State == ExportTransactionState.Completed)
            {
                // Completed is terminal. A later export is allowed to replace
                // the root/latest aliases and must not cause an older run to
                // be replayed merely because its descriptor is bound to its
                // own historical artifact-index snapshot. Explicit verify or
                // repair can inspect/rebuild damaged derived metadata.
                continue;
            }

            if (document.State == ExportTransactionState.RolledBack
                || string.Equals(document.FailureCode, "export-rolled-back", StringComparison.Ordinal))
            {
                // A deliberate rollback is a terminal, clean outcome. Keep
                // the document as an audit marker, but never resurrect its
                // Journal into a new metadata commit on the next startup.
                TryDeleteDirectory(ExportPathSafety.CombineUnderRoot(_exportRoot, "runs", "." + document.RunId + ".staging"));
                continue;
            }

            var updatedItems = new List<ExportTransactionItem>(document.Items.Count);
            var allResolved = true;
            var changed = false;
            foreach (var item in document.Items)
            {
                var resolved = await ReconcileTransactionItemAsync(item, cancellationToken).ConfigureAwait(false);
                updatedItems.Add(resolved.Item);
                allResolved &= resolved.Resolved;
                changed |= !Equals(resolved.Item, item);
            }

            var pendingJournalPath = ExportPathSafety.CombineUnderRoot(_exportRoot, "runs", document.RunId + ".jsonl");
            if (!File.Exists(pendingJournalPath))
            {
                throw new IOException($"Export transaction '{document.RunId}' has no Journal and cannot be recovered safely.");
            }

            if (allResolved)
            {
                var stagingRoot = ExportPathSafety.CombineUnderRoot(_exportRoot, "runs", "." + document.RunId + ".staging");
                TryDeleteDirectory(stagingRoot);
                var recovered = new ExportTransactionDocument(
                    document.RunId,
                    document.OperationId,
                    document.SelectionFingerprint,
                    document.State is ExportTransactionState.ArtifactsCommitted or ExportTransactionState.MetadataCommitted
                        ? document.State
                        : ExportTransactionState.ArtifactsCommitted,
                    DateTimeOffset.UtcNow,
                    updatedItems,
                    document.MetadataCommit,
                    null);
                if (changed || recovered.State != document.State)
                {
                    await AtomicFileWriter.WriteJsonAsync(transactionPath, recovered, InfrastructureJson.Indented, cancellationToken).ConfigureAwait(false);
                }

                await AppendRecoveredItemEventsAsync(recovered, cancellationToken).ConfigureAwait(false);
                var journalPath = pendingJournalPath;
                if (resumeMetadata
                    && File.Exists(journalPath)
                    && recovered.State is ExportTransactionState.ArtifactsCommitted or ExportTransactionState.MetadataCommitted)
                {
                    // Resume the metadata phase while the caller still owns
                    // the exclusive root lock. This closes the crash window
                    // between artifact publication and manifest-committed.
                    await RecoverRunUnderLockAsync(journalPath, document.RunId, cancellationToken).ConfigureAwait(false);
                }
            }
            else if (changed)
            {
                var recovered = new ExportTransactionDocument(
                    document.RunId,
                    document.OperationId,
                    document.SelectionFingerprint,
                    ExportTransactionState.FailedRecoverable,
                    DateTimeOffset.UtcNow,
                    updatedItems,
                    document.MetadataCommit,
                    "export-recovery-required");
                await AtomicFileWriter.WriteJsonAsync(transactionPath, recovered, InfrastructureJson.Indented, cancellationToken).ConfigureAwait(false);
                throw new IOException($"Export transaction '{document.RunId}' contains an artifact that cannot be recovered safely.");
            }
            else if (!allResolved)
            {
                throw new IOException($"Export transaction '{document.RunId}' contains an artifact that cannot be recovered safely.");
            }
        }

        CleanupOrphanedStagingDirectories(runsDirectory, cancellationToken);
    }

    /// <summary>
    /// Removes only abandoned staging directories that are not referenced by a
    /// recoverable transaction. Active and failed-recoverable transactions are
    /// deliberately retained for explicit reconciliation; an old directory is
    /// not proof that its contents are disposable.
    /// </summary>
    private static void CleanupOrphanedStagingDirectories(
        string runsDirectory,
        CancellationToken cancellationToken)
    {
        var activeRunIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var transactionPath in Directory.EnumerateFiles(runsDirectory, "*.transaction.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var stream = new FileStream(transactionPath, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, FileOptions.SequentialScan);
                var transaction = JsonSerializer.Deserialize<ExportTransactionDocument>(stream, InfrastructureJson.Compact);
                if (transaction is not null
                    && transaction.State is not (ExportTransactionState.Completed or ExportTransactionState.RolledBack))
                {
                    activeRunIds.Add(transaction.RunId);
                }
            }
            catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
            {
                // A malformed transaction must remain visible for fail-closed
                // recovery and must never cause its staging files to be deleted.
                var fileName = Path.GetFileName(transactionPath);
                if (fileName.EndsWith(".transaction.json", StringComparison.Ordinal))
                {
                    activeRunIds.Add(fileName[..^".transaction.json".Length]);
                }
            }
        }

        var cutoff = DateTime.UtcNow - TimeSpan.FromDays(1);
        foreach (var directory in Directory.EnumerateDirectories(runsDirectory, ".*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(directory);
            string? runId = null;
            if (name.StartsWith(".", StringComparison.Ordinal)
                && name.EndsWith(".metadata.staging", StringComparison.Ordinal))
            {
                runId = name[1..^".metadata.staging".Length];
            }
            else if (name.StartsWith(".", StringComparison.Ordinal)
                && name.EndsWith(".staging", StringComparison.Ordinal))
            {
                runId = name[1..^".staging".Length];
            }

            if (string.IsNullOrWhiteSpace(runId)
                || activeRunIds.Contains(runId)
                || Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0
                || Directory.GetLastWriteTimeUtc(directory) > cutoff)
            {
                continue;
            }

            TryDeleteDirectory(directory);
        }
    }

    private async Task AppendRecoveredItemEventsAsync(
        ExportTransactionDocument document,
        CancellationToken cancellationToken)
    {
        var journalPath = ExportPathSafety.CombineUnderRoot(_exportRoot, "runs", document.RunId + ".jsonl");
        if (!File.Exists(journalPath))
        {
            return;
        }

        var committed = new HashSet<string>(StringComparer.Ordinal);
        await using (var stream = new FileStream(journalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 32 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        using (var reader = new StreamReader(stream, Encoding.UTF8))
        {
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                try
                {
                    var existing = JsonSerializer.Deserialize<VoiceExportJournalEvent>(line, InfrastructureJson.Compact);
                    if (existing is { Event: "item-committed" or "item-skipped", MessageId: not null })
                    {
                        committed.Add(existing.MessageId);
                    }
                }
                catch (JsonException)
                {
                    // The recovery reader already treats a torn final line as
                    // recoverable. New events are appended on a fresh line.
                }
            }
        }

        var pending = document.Items
            .Where(item => item.Entry is not null && !committed.Contains(item.MessageId))
            .Select(item => new VoiceExportJournalEvent(
                item.Entry!.WasSkipped ? "item-skipped" : "item-committed",
                document.RunId,
                DateTimeOffset.UtcNow,
                item.MessageId,
                Entry: item.Entry))
            .ToArray();
        if (pending.Length == 0)
        {
            return;
        }

        var text = string.Join(
            string.Empty,
            pending.Select(item => JsonSerializer.Serialize(item, InfrastructureJson.Compact) + Environment.NewLine));
        await using (var stream = new FileStream(
            journalPath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            32 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var bytes = Encoding.UTF8.GetBytes(Environment.NewLine + text);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
    }

    private async Task<(ExportTransactionItem Item, bool Resolved)> ReconcileTransactionItemAsync(
        ExportTransactionItem item,
        CancellationToken cancellationToken)
    {
        var original = await ReconcileArtifactAsync(
            item.StagedOriginalPath,
            item.FinalOriginalPath,
            item.OriginalByteLength,
            item.OriginalSha256,
            item.OriginalPublishState,
            item.PreviousOriginalState,
            cancellationToken).ConfigureAwait(false);
        var decoded = string.IsNullOrWhiteSpace(item.FinalDecodedPath)
            ? (Result: new ArtifactRecoveryResult(ExportPublishState.NotStarted), Resolved: true)
            : await ReconcileArtifactAsync(
                item.StagedDecodedPath,
                item.FinalDecodedPath,
                item.DecodedByteLength,
                item.DecodedSha256,
                item.DecodedPublishState,
                item.PreviousDecodedState,
                cancellationToken).ConfigureAwait(false);
        var updated = item with
        {
            OriginalByteLength = original.Result.ByteLength ?? item.OriginalByteLength,
            OriginalSha256 = original.Result.Sha256 ?? item.OriginalSha256,
            DecodedByteLength = decoded.Result.ByteLength ?? item.DecodedByteLength,
            DecodedSha256 = decoded.Result.Sha256 ?? item.DecodedSha256,
            OriginalPublishState = original.Result.State,
            DecodedPublishState = decoded.Result.State,
        };
        return (updated, original.Resolved && decoded.Resolved);
    }

    private async Task<(ArtifactRecoveryResult Result, bool Resolved)> ReconcileArtifactAsync(
        string? stagedRelativePath,
        string? finalRelativePath,
        long? expectedLength,
        string? expectedSha256,
        ExportPublishState state,
        ExportArtifactState previousState,
        CancellationToken cancellationToken)
    {
        if (state == ExportPublishState.Existing
            || previousState == ExportArtifactState.VerifiedExisting && string.IsNullOrWhiteSpace(stagedRelativePath))
        {
            if (string.IsNullOrWhiteSpace(finalRelativePath)
                || string.IsNullOrWhiteSpace(expectedSha256))
            {
                return (new ArtifactRecoveryResult(ExportPublishState.Failed), false);
            }

            var existingPath = ResolveTransactionPath(finalRelativePath);
            if (!File.Exists(existingPath)
                || (File.GetAttributes(existingPath) & FileAttributes.ReparsePoint) != 0)
            {
                return (new ArtifactRecoveryResult(ExportPublishState.Failed), false);
            }

            var existingMetadata = await FileHashing.ComputeMetadataAsync(existingPath, cancellationToken).ConfigureAwait(false);
            return Matches(existingMetadata, expectedLength, expectedSha256)
                ? (new ArtifactRecoveryResult(ExportPublishState.Existing, existingMetadata.ByteLength, existingMetadata.Sha256), true)
                : (new ArtifactRecoveryResult(ExportPublishState.Failed, existingMetadata.ByteLength, existingMetadata.Sha256), false);
        }

        if (string.IsNullOrWhiteSpace(finalRelativePath))
        {
            return (new ArtifactRecoveryResult(ExportPublishState.NotStarted), true);
        }

        var finalPath = ResolveTransactionPath(finalRelativePath);
        var stagedPath = string.IsNullOrWhiteSpace(stagedRelativePath) ? null : ResolveTransactionPath(stagedRelativePath);
        var finalMetadata = File.Exists(finalPath)
            ? (File.GetAttributes(finalPath) & FileAttributes.ReparsePoint) != 0
                ? null
                : await FileHashing.ComputeMetadataAsync(finalPath, cancellationToken).ConfigureAwait(false)
            : null;
        if (File.Exists(finalPath) && finalMetadata is null)
        {
            return (new ArtifactRecoveryResult(ExportPublishState.Failed), false);
        }
        if (finalMetadata is not null && Matches(finalMetadata, expectedLength, expectedSha256))
        {
            if (stagedPath is not null && File.Exists(stagedPath))
            {
                File.Delete(stagedPath);
            }

            return (new ArtifactRecoveryResult(ExportPublishState.Committed, finalMetadata.ByteLength, finalMetadata.Sha256), true);
        }

        if (stagedPath is null || !File.Exists(stagedPath))
        {
            return (new ArtifactRecoveryResult(ExportPublishState.Failed), false);
        }

        if ((File.GetAttributes(stagedPath) & FileAttributes.ReparsePoint) != 0)
        {
            return (new ArtifactRecoveryResult(ExportPublishState.Failed), false);
        }

        var stagedMetadata = await FileHashing.ComputeMetadataAsync(stagedPath, cancellationToken).ConfigureAwait(false);
        if (!Matches(stagedMetadata, expectedLength, expectedSha256))
        {
            return (new ArtifactRecoveryResult(ExportPublishState.Failed, stagedMetadata.ByteLength, stagedMetadata.Sha256), false);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        if (!File.Exists(finalPath))
        {
            File.Move(stagedPath, finalPath);
        }

        var committedMetadata = await FileHashing.ComputeMetadataAsync(finalPath, cancellationToken).ConfigureAwait(false);
        return Matches(committedMetadata, expectedLength, expectedSha256)
            ? (new ArtifactRecoveryResult(ExportPublishState.Committed, committedMetadata.ByteLength, committedMetadata.Sha256), true)
            : (new ArtifactRecoveryResult(ExportPublishState.Failed, committedMetadata.ByteLength, committedMetadata.Sha256), false);
    }

    private string ResolveTransactionPath(string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException("An export transaction contains an absolute path.");
        }

        return ExportPathSafety.CombineUnderRoot(_exportRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    private static bool Matches(FileHashMetadata metadata, long? expectedLength, string? expectedSha256)
        => (expectedLength is null || metadata.ByteLength == expectedLength.Value)
            && (string.IsNullOrWhiteSpace(expectedSha256) || string.Equals(metadata.Sha256, expectedSha256, StringComparison.OrdinalIgnoreCase));

    private static async Task<T> ReadJsonDocumentAsync<T>(
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

        return await JsonSerializer.DeserializeAsync<T>(
                   stream,
                   InfrastructureJson.Compact,
                   cancellationToken)
               .ConfigureAwait(false)
               ?? throw new InvalidDataException($"The JSON document '{path}' is empty.");
    }

    private static async Task<bool> JournalHasManifestCommitAsync(
        string journalPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(journalPath))
        {
            return false;
        }

        await using var stream = new FileStream(
            journalPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var journalEvent = JsonSerializer.Deserialize<VoiceExportJournalEvent>(line, InfrastructureJson.Compact);
                if (journalEvent?.Event == "manifest-committed")
                {
                    return true;
                }
            }
            catch (JsonException)
            {
                // ReadManifestFromJournalAsync performs the strict malformed
                // line validation immediately before this helper is called.
            }
        }

        return false;
    }

    private static async Task AppendJournalEventDurablyAsync(
        string journalPath,
        VoiceExportJournalEvent journalEvent,
        CancellationToken cancellationToken)
    {
        var line = JsonSerializer.Serialize(journalEvent, InfrastructureJson.Compact) + Environment.NewLine;
        var bytes = Encoding.UTF8.GetBytes(line);
        await using var stream = new FileStream(
            journalPath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private async Task<bool> MetadataCommitIsCompleteAsync(
        string runId,
        CancellationToken cancellationToken,
        bool requireLatestAliases = true)
    {
        var descriptorPath = ExportPathSafety.CombineUnderRoot(_exportRoot, "runs", runId + ".metadata-commit.json");
        if (!File.Exists(descriptorPath))
        {
            return false;
        }

        ExportMetadataCommitDescriptor descriptor;
        try
        {
            descriptor = await ReadJsonDocumentAsync<ExportMetadataCommitDescriptor>(descriptorPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
        {
            return false;
        }

        if (!string.Equals(descriptor.RunId, runId, StringComparison.Ordinal))
        {
            return false;
        }

        var files = new[]
        {
            (Path: ExportPathSafety.CombineUnderRoot(_exportRoot, "runs", ExportManifestLayout.RunPrivateManifestFileName(runId)), Hash: descriptor.PrivateManifestSha256),
            (Path: ExportPathSafety.CombineUnderRoot(_exportRoot, "runs", ExportManifestLayout.RunPortableManifestFileName(runId)), Hash: descriptor.PortableManifestSha256),
            (Path: ExportPathSafety.CombineUnderRoot(_exportRoot, "runs", ExportManifestLayout.RunPortableCsvFileName(runId)), Hash: descriptor.DatasetCsvSha256),
            (Path: ExportPathSafety.CombineUnderRoot(_exportRoot, "runs", ExportManifestLayout.RunArtifactIndexFileName(runId)), Hash: descriptor.ArtifactIndexSha256),
        };
        foreach (var file in files)
        {
            if (!File.Exists(file.Path))
            {
                return false;
            }

            var hash = await FileHashing.ComputeSha256Async(file.Path, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(hash, file.Hash, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (!requireLatestAliases)
        {
            return true;
        }

        var latestFiles = new[]
        {
            (Path: ExportPathSafety.CombineUnderRoot(_exportRoot, ExportManifestLayout.PrivateManifestFileName), Hash: descriptor.PrivateManifestSha256),
            (Path: ExportPathSafety.CombineUnderRoot(_exportRoot, ExportManifestLayout.PortableManifestFileName), Hash: descriptor.PortableManifestSha256),
            (Path: ExportPathSafety.CombineUnderRoot(_exportRoot, ExportManifestLayout.PortableCsvFileName), Hash: descriptor.DatasetCsvSha256),
            (Path: ExportPathSafety.CombineUnderRoot(_exportRoot, "artifact-index.jsonl"), Hash: descriptor.ArtifactIndexSha256),
        };
        foreach (var file in latestFiles)
        {
            if (!File.Exists(file.Path))
            {
                return false;
            }

            var hash = await FileHashing.ComputeSha256Async(file.Path, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(hash, file.Hash, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        var latestDescriptorPath = ExportPathSafety.CombineUnderRoot(_exportRoot, "latest.metadata-commit.json");
        if (!File.Exists(latestDescriptorPath))
        {
            return false;
        }

        try
        {
            var latest = await ReadJsonDocumentAsync<ExportMetadataCommitDescriptor>(latestDescriptorPath, cancellationToken).ConfigureAwait(false);
            return latest == descriptor;
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
        {
            return false;
        }
    }

    private static async Task MarkTransactionCompletedAsync(
        string transactionPath,
        string runId,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(transactionPath))
        {
            return;
        }

        var transaction = await ReadJsonDocumentAsync<ExportTransactionDocument>(transactionPath, cancellationToken).ConfigureAwait(false);
        if (transaction.State == ExportTransactionState.Completed)
        {
            return;
        }

        ExportMetadataCommitDescriptor? descriptor = transaction.MetadataCommit;
        var descriptorPath = Path.Combine(Path.GetDirectoryName(transactionPath)!, runId + ".metadata-commit.json");
        if (File.Exists(descriptorPath))
        {
            try
            {
                descriptor = await ReadJsonDocumentAsync<ExportMetadataCommitDescriptor>(descriptorPath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
            {
                // Preserve the transaction's previous descriptor. Verification
                // will report a missing/invalid metadata commit if needed.
            }
        }

        await AtomicFileWriter.WriteJsonAsync(
            transactionPath,
            new ExportTransactionDocument(
                transaction.RunId,
                transaction.OperationId,
                transaction.SelectionFingerprint,
                ExportTransactionState.Completed,
                DateTimeOffset.UtcNow,
                transaction.Items,
                descriptor),
            InfrastructureJson.Indented,
            cancellationToken).ConfigureAwait(false);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void DeleteFileIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void MoveFileReplacing(string sourcePath, string destinationPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        if (!File.Exists(destinationPath))
        {
            File.Move(sourcePath, destinationPath);
            return;
        }

        var backupPath = destinationPath + ".backup-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Replace(sourcePath, destinationPath, backupPath, ignoreMetadataErrors: true);
        }
        finally
        {
            try
            {
                if (File.Exists(backupPath))
                {
                    File.Delete(backupPath);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed record ArtifactRecoveryResult(
        ExportPublishState State,
        long? ByteLength = null,
        string? Sha256 = null);

    internal static async Task<VoiceExportManifest> ReadManifestFromJournalAsync(
        VoiceExportManifest fallback,
        string journalPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(journalPath))
        {
            return fallback;
        }

        var entries = new List<VoiceExportEntry>();
        var failures = new List<VoiceExportFailure>();
        VoiceCatalogContext? journalContext = null;
        var runStatus = fallback.RunStatus;
        DateTimeOffset? manifestGeneratedAtUtc = null;
        await using var journalStream = new FileStream(journalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var journalReader = new StreamReader(journalStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, 128 * 1024, leaveOpen: false);
        var journalText = await journalReader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        var journalLines = journalText.Split('\n');
        for (var lineIndex = 0; lineIndex < journalLines.Length; lineIndex++)
        {
            var line = journalLines[lineIndex].TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            VoiceExportJournalEvent? journalEvent;
            try
            {
                journalEvent = JsonSerializer.Deserialize<VoiceExportJournalEvent>(line, InfrastructureJson.Compact);
            }
            catch (JsonException)
            {
                // A process crash can leave a truncated final JSONL line. A
                // malformed line in the middle is corruption and must not be
                // silently skipped.
                var isFinalTruncatedLine = lineIndex == journalLines.Length - 1 && !journalText.EndsWith('\n');
                if (!isFinalTruncatedLine)
                {
                    throw new InvalidDataException("The export Journal contains a malformed non-final JSONL line.");
                }

                break;
            }
            if (journalEvent is null)
            {
                throw new InvalidDataException("The export Journal contains a null event.");
            }

            if (!string.Equals(journalEvent.RunId, fallback.RunId, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"The export Journal event RunId '{journalEvent.RunId}' does not match '{fallback.RunId}'.");
            }

            if (journalEvent.Entry is { } entry && journalEvent.Event is "item-committed" or "item-skipped")
            {
                entries.Add(entry);
            }

            if (journalEvent.Context is { } context)
            {
                journalContext = context;
            }

            if (journalEvent.Event == "manifest-committed")
            {
                manifestGeneratedAtUtc = journalEvent.ManifestGeneratedAtUtc;
            }

            if (journalEvent.Failure is { } failure && journalEvent.Event == "item-failed")
            {
                failures.Add(failure);
            }

            runStatus = journalEvent.Event switch
            {
                "run-cancelled" => ExportRunStatus.Cancelled,
                "run-failed" => ExportRunStatus.Failed,
                "processing-completed" => failures.Count > 0 ? ExportRunStatus.CompletedWithFailures : ExportRunStatus.Completed,
                _ => runStatus,
            };
        }

        return new VoiceExportManifest(
            manifestGeneratedAtUtc ?? fallback.GeneratedAtUtc,
            entries.OrderBy(static entry => entry.OccurredAtUtc).ThenBy(static entry => entry.MessageId, StringComparer.Ordinal).ToArray(),
            failures.OrderBy(static failure => failure.MessageId ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static failure => failure.Stage, StringComparer.Ordinal)
                .ThenBy(static failure => failure.Error, StringComparer.Ordinal).ToArray(),
            fallback.RunId,
            journalContext?.SnapshotId ?? fallback.SnapshotId,
            journalContext?.AdapterId ?? fallback.AdapterId,
            journalContext?.AccountId ?? fallback.AccountId,
            journalContext?.DatasetId ?? fallback.DatasetId,
            journalContext?.AdapterVersion ?? fallback.AdapterVersion,
            journalContext?.DatabaseFingerprints ?? fallback.DatabaseFingerprints,
            runStatus,
            runStatus == ExportRunStatus.Cancelled,
            journalContext?.MaterializationProvenance ?? fallback.Provenance,
            journalContext?.AccountIdentity ?? fallback.AccountIdentity);
    }

    private async Task<ExportArtifact?> ReadExistingArtifactAsync(string path, string relativePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new ExistingArtifactConflictException("An export artifact cannot be a reparse point.");
        }

        var info = new FileInfo(path);
        await _artifactIndexGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureArtifactIndexLoadedAsync(cancellationToken).ConfigureAwait(false);
            var lastWriteTicks = info.LastWriteTimeUtc.Ticks;
            var fileId = ArtifactFileIdentity.Read(path);
            // The index is deliberately only a reuse hint for bookkeeping.
            // File ID, length, and timestamps are not cryptographic evidence:
            // a file can be rewritten in place and have its timestamp restored.
            // Always hash the current bytes before declaring an existing
            // artifact VerifiedExisting.
            var sha256 = await FileHashing.ComputeSha256Async(path, cancellationToken).ConfigureAwait(false);
            var entry = new ArtifactIndexEntry(relativePath, fileId, info.Length, lastWriteTicks, sha256, DateTimeOffset.UtcNow);
            _artifactIndex![relativePath] = entry;
            return new ExportArtifact(relativePath, info.Length, sha256);
        }
        finally
        {
            _artifactIndexGate.Release();
        }
    }

    private async Task EnsureArtifactIndexLoadedAsync(CancellationToken cancellationToken)
    {
        if (_artifactIndex is not null)
        {
            return;
        }

        _artifactIndex = new Dictionary<string, ArtifactIndexEntry>(StringComparer.OrdinalIgnoreCase);
        var indexPath = Path.Combine(_exportRoot, "artifact-index.jsonl");
        if (!File.Exists(indexPath))
        {
            return;
        }

        await using var stream = new FileStream(indexPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 32 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var entry = JsonSerializer.Deserialize<ArtifactIndexEntry>(line, InfrastructureJson.Compact);
                if (entry is not null && !string.IsNullOrWhiteSpace(entry.RelativePath))
                {
                    _artifactIndex[entry.RelativePath] = entry;
                }
            }
            catch (JsonException)
            {
                // A torn final index line is ignored; the next verification
                // will replace the entry with a complete record.
            }
        }
    }

    private sealed record ArtifactIndexEntry(
        string RelativePath,
        string? FileId,
        long Length,
        long LastWriteUtcTicks,
        string Sha256,
        DateTimeOffset LastVerifiedUtc);

    private static ExportArtifactState GetOriginalState(ExportArtifact? artifact, VoiceRecord record)
    {
        if (artifact is null)
        {
            return ExportArtifactState.Missing;
        }

        var hashMatches = string.IsNullOrWhiteSpace(record.PayloadSha256)
            ? false
            : string.Equals(artifact.Sha256, record.PayloadSha256, StringComparison.OrdinalIgnoreCase);
        var lengthMatches = record.PayloadByteLength is null || artifact.ByteLength == record.PayloadByteLength.Value;
        if (hashMatches && lengthMatches)
        {
            return ExportArtifactState.VerifiedExisting;
        }

        if (string.IsNullOrWhiteSpace(record.PayloadSha256) && lengthMatches)
        {
            // The source hash is unknown, so identity can only be decided at
            // commit time against the freshly computed source bytes.
            return ExportArtifactState.PendingExisting;
        }

        return ExportArtifactState.Conflict;
    }

    private static async Task<ExportArtifactState> GetDecodedStateAsync(ExportArtifact? artifact, VoiceRecord record, string path, CancellationToken cancellationToken)
    {
        if (artifact is null)
        {
            return ExportArtifactState.Missing;
        }

        var hasKnownHash = !string.IsNullOrWhiteSpace(record.DecodedSha256);
        var hashMatches = !hasKnownHash
            || string.Equals(artifact.Sha256, record.DecodedSha256, StringComparison.OrdinalIgnoreCase);
        var lengthMatches = record.DecodedByteLength is null || artifact.ByteLength == record.DecodedByteLength.Value;
        var wavValid = await WavFileValidator.IsValidAsync(path, cancellationToken).ConfigureAwait(false);
        return hashMatches && lengthMatches && (hasKnownHash || wavValid) ? ExportArtifactState.VerifiedExisting : ExportArtifactState.Conflict;
    }

    private void Reserve(string path, string sourceStableKey)
    {
        if (!_reservedPaths.TryAdd(path, 0))
        {
            throw new ExistingArtifactConflictException($"An export for source key '{sourceStableKey}' is already in progress.");
        }
    }

    private void Release(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            _reservedPaths.TryRemove(path, out _);
        }
    }

    private async Task<DirectArtifactCommitResult> CommitDirectArtifactAsync(
        string temporaryPath,
        string finalPath,
        ExportArtifact expectedArtifact,
        bool replace,
        bool pendingExisting,
        CancellationToken cancellationToken)
    {
        await using var rootLock = await ExportRootLock.AcquireAsync(
            _exportRoot,
            ExportRootLockMode.Exclusive,
            Guid.NewGuid().ToString("N"),
            runId: null,
            cancellationToken,
            waitForAvailability: true).ConfigureAwait(false);

        if (File.Exists(finalPath))
        {
            if ((File.GetAttributes(finalPath) & FileAttributes.ReparsePoint) != 0)
            {
                throw new ExistingArtifactConflictException("The existing export artifact is a reparse point.");
            }

            var existing = await FileHashing.ComputeMetadataAsync(finalPath, cancellationToken).ConfigureAwait(false);
            if (!replace)
            {
                if (existing.ByteLength == expectedArtifact.ByteLength
                    && string.Equals(existing.Sha256, expectedArtifact.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(temporaryPath);
                    return new DirectArtifactCommitResult(
                        new ExportArtifact(expectedArtifact.RelativePath, existing.ByteLength, existing.Sha256),
                        Existing: true);
                }

                if (pendingExisting)
                {
                    TryDeleteFile(temporaryPath);
                    throw new SourceContentMismatchException(
                        "original",
                        existing.ByteLength,
                        expectedArtifact.ByteLength,
                        existing.Sha256,
                        expectedArtifact.Sha256);
                }

                TryDeleteFile(temporaryPath);
                throw new ExistingArtifactConflictException("Another export published a different artifact for the same stable source key.");
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        if (replace)
        {
            FileSystemExportItemLease.AtomicCommit(temporaryPath, finalPath);
        }
        else
        {
            // The lock and the absence check above make this a create-only
            // publication. If an unrelated process changes the path outside
            // this protocol, File.Move fails closed instead of replacing it.
            File.Move(temporaryPath, finalPath);
        }

        var committed = await FileHashing.ComputeMetadataAsync(finalPath, cancellationToken).ConfigureAwait(false);
        if (committed.ByteLength != expectedArtifact.ByteLength
            || !string.Equals(committed.Sha256, expectedArtifact.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new SourceContentMismatchException(
                "export",
                expectedArtifact.ByteLength,
                committed.ByteLength,
                expectedArtifact.Sha256,
                committed.Sha256);
        }

        return new DirectArtifactCommitResult(
            new ExportArtifact(expectedArtifact.RelativePath, committed.ByteLength, committed.Sha256),
            Existing: false);
    }

    private sealed record DirectArtifactCommitResult(ExportArtifact Artifact, bool Existing);

    private sealed class FileSystemExportItemLease : IExportItemLease
    {
        private readonly FileSystemVoiceExportStore _store;
        private readonly string _originalPath;
        private readonly string _decodedPath;
        private readonly string _finalOriginalPath;
        private readonly string _finalDecodedPath;
        private readonly bool _replace;
        private readonly bool _deferPublish;
        private readonly bool _originalExistedAtStart;
        private readonly bool _decodedExistedAtStart;
        private readonly Action _release;
        private ExportArtifactState _originalState;
        private ExportArtifactState _decodedState;
        private ExportArtifact? _existingDecodedArtifact;
        private string? _originalTemporaryPath;
        private string? _decodedTemporaryPath;
        private bool _originalCommitted;
        private bool _decodedCommitted;
        private bool _publishedOriginal;
        private bool _publishedDecoded;
        private bool _originalPublishing;
        private bool _decodedPublishing;
        private bool _invalidateDecodedOnPublish;
        private ExportArtifact? _committedOriginalArtifact;
        private ExportArtifact? _committedDecodedArtifact;
        private VoiceExportEntry? _entry;
        private bool _disposed;
        private readonly Func<FileSystemExportItemLease, CancellationToken, Task>? _changed;

        public FileSystemExportItemLease(
            FileSystemVoiceExportStore store,
            VoiceRecord record,
            string originalManifestPath,
            string decodedManifestPath,
            string originalPath,
            string decodedPath,
            string finalOriginalPath,
            string finalDecodedPath,
            ExportArtifactState originalState,
            ExportArtifactState decodedState,
            ExportArtifact? existingOriginalArtifact,
            ExportArtifact? existingDecodedArtifact,
            bool replace,
            bool deferPublish,
            Action release,
            Func<FileSystemExportItemLease, CancellationToken, Task>? changed)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            Record = record;
            OriginalManifestPath = originalManifestPath;
            DecodedManifestPath = decodedManifestPath;
            _originalPath = originalPath;
            _decodedPath = decodedPath;
            _finalOriginalPath = finalOriginalPath;
            _finalDecodedPath = finalDecodedPath;
            _originalState = originalState;
            _decodedState = decodedState;
            ExistingOriginalArtifact = existingOriginalArtifact;
            _existingDecodedArtifact = existingDecodedArtifact;
            _replace = replace;
            _deferPublish = deferPublish;
            _originalExistedAtStart = existingOriginalArtifact is not null;
            _decodedExistedAtStart = existingDecodedArtifact is not null;
            _release = release;
            _changed = changed;
        }

        public VoiceRecord Record { get; }
        public ExportArtifactState OriginalState => _originalState;
        public ExportArtifactState DecodedState => _decodedState;
        public ExportArtifact? ExistingOriginalArtifact { get; }
        public ExportArtifact? ExistingDecodedArtifact => _existingDecodedArtifact;
        public string OriginalManifestPath { get; }
        public string DecodedManifestPath { get; }
        internal VoiceExportEntry? Entry => _entry;

        public ValueTask<Stream> OpenOriginalWriteAsync(CancellationToken cancellationToken)
            => OpenWriteAsync(isDecoded: false, cancellationToken);

        public ValueTask<Stream> OpenOriginalReadAsync(CancellationToken cancellationToken)
        {
            EnsureUsable();
            if (!_originalCommitted && OriginalState == ExportArtifactState.Conflict)
            {
                throw new InvalidOperationException("The original artifact is not available for reading.");
            }

            var path = _originalCommitted ? _originalPath : _finalOriginalPath;
            return ValueTask.FromResult<Stream>(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan));
        }

        public Task<ExportArtifact> CommitOriginalAsync(CancellationToken cancellationToken)
            => CommitAsync(isDecoded: false, cancellationToken);

        public Task<ExportArtifact> CommitOriginalAsync(ExportArtifact computedArtifact, CancellationToken cancellationToken)
            => CommitAsync(isDecoded: false, cancellationToken, computedArtifact);

        public ValueTask<Stream> OpenDecodedWriteAsync(CancellationToken cancellationToken)
            => OpenWriteAsync(isDecoded: true, cancellationToken);

        public Task<ExportArtifact> CommitDecodedAsync(CancellationToken cancellationToken)
            => CommitAsync(isDecoded: true, cancellationToken);

        public Task RollbackAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeleteTemporary(ref _originalTemporaryPath);
            DeleteTemporary(ref _decodedTemporaryPath);
            return Task.CompletedTask;
        }

        internal async Task PublishStagedAsync(
            Func<FileSystemExportItemLease, CancellationToken, Task>? changed,
            CancellationToken cancellationToken)
        {
            if (!_deferPublish)
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(_finalOriginalPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(_finalDecodedPath)!);
            if (_originalCommitted && File.Exists(_originalPath))
            {
                _originalPublishing = true;
                if (changed is not null) await changed(this, cancellationToken).ConfigureAwait(false);
                AtomicCommit(_originalPath, _finalOriginalPath);
                _publishedOriginal = true;
                _originalPublishing = false;
                if (changed is not null) await changed(this, cancellationToken).ConfigureAwait(false);
            }

            if (_invalidateDecodedOnPublish && File.Exists(_finalDecodedPath))
            {
                File.Delete(_finalDecodedPath);
            }

            if (_decodedCommitted && File.Exists(_decodedPath))
            {
                _decodedPublishing = true;
                if (changed is not null) await changed(this, cancellationToken).ConfigureAwait(false);
                AtomicCommit(_decodedPath, _finalDecodedPath);
                _publishedDecoded = true;
                _decodedPublishing = false;
                if (changed is not null) await changed(this, cancellationToken).ConfigureAwait(false);
            }

            await Task.CompletedTask.ConfigureAwait(false);
        }

        internal void RollbackPublished()
        {
            if (!_deferPublish)
            {
                return;
            }

            // The normal SkipIfHashMatches transaction only publishes new
            // paths. Existing verified artifacts are intentionally untouched
            // if a later item fails. Replace semantics remains supported by
            // the direct store API; it is not used by the application run
            // transaction because multi-file replacement cannot be restored
            // portably after an interrupted commit.
            if (_publishedDecoded && !_decodedExistedAtStart)
            {
                DeleteTemporaryPath(_finalDecodedPath);
            }

            if (_publishedOriginal && !_originalExistedAtStart)
            {
                DeleteTemporaryPath(_finalOriginalPath);
            }
        }

        internal ExportTransactionItem CreateTransactionItem(string exportRoot)
        {
            var originalState = _originalPublishing
                ? ExportPublishState.Publishing
                : _publishedOriginal
                    ? ExportPublishState.Committed
                    : _originalState == ExportArtifactState.VerifiedExisting
                        ? ExportPublishState.Existing
                        : ExportPublishState.NotStarted;
            var decodedState = _decodedPublishing
                ? ExportPublishState.Publishing
                : _publishedDecoded
                    ? ExportPublishState.Committed
                    : _decodedState == ExportArtifactState.VerifiedExisting
                        ? ExportPublishState.Existing
                        : ExportPublishState.NotStarted;
            return new ExportTransactionItem(
                Record.MessageId,
                Record.SourceStableKey,
                Relative(exportRoot, _originalPath),
                Relative(exportRoot, _finalOriginalPath),
                _decodedCommitted || _decodedTemporaryPath is not null ? Relative(exportRoot, _decodedPath) : null,
                _decodedCommitted || ExistingDecodedArtifact is not null ? Relative(exportRoot, _finalDecodedPath) : null,
                _committedOriginalArtifact?.ByteLength ?? ExistingOriginalArtifact?.ByteLength,
                _committedOriginalArtifact?.Sha256 ?? ExistingOriginalArtifact?.Sha256,
                _committedDecodedArtifact?.ByteLength ?? ExistingDecodedArtifact?.ByteLength,
                _committedDecodedArtifact?.Sha256 ?? ExistingDecodedArtifact?.Sha256,
                originalState,
                decodedState,
                OriginalState,
                DecodedState,
                _entry);
        }

        internal void SetEntry(VoiceExportEntry entry) => _entry = entry;

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                await RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _release();
            }
        }

        private ValueTask<Stream> OpenWriteAsync(bool isDecoded, CancellationToken cancellationToken)
        {
            EnsureUsable();
            var state = isDecoded ? DecodedState : OriginalState;
            var committed = isDecoded ? _decodedCommitted : _originalCommitted;
            var temporary = isDecoded ? _decodedTemporaryPath : _originalTemporaryPath;
            if (!_replace && state == ExportArtifactState.VerifiedExisting)
            {
                throw new InvalidOperationException("The verified existing artifact does not need to be rewritten.");
            }

            if (state == ExportArtifactState.Conflict && !_replace)
            {
                throw new ExistingArtifactConflictException("The existing artifact conflicts with the source expectation.");
            }

            if (committed || temporary is not null)
            {
                throw new InvalidOperationException("The artifact has already been opened or committed.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var finalPath = isDecoded ? _decodedPath : _originalPath;
            var temporaryPath = Path.Combine(Path.GetDirectoryName(finalPath)!, $".{Path.GetFileName(finalPath)}.{Guid.NewGuid():N}.tmp");
            if (isDecoded)
            {
                _decodedTemporaryPath = temporaryPath;
            }
            else
            {
                _originalTemporaryPath = temporaryPath;
            }

            Stream stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return ValueTask.FromResult(stream);
        }

        private async Task<ExportArtifact> CommitAsync(
            bool isDecoded,
            CancellationToken cancellationToken,
            ExportArtifact? computedArtifact = null)
        {
            EnsureUsable();
            cancellationToken.ThrowIfCancellationRequested();
            var temporaryPath = isDecoded ? _decodedTemporaryPath : _originalTemporaryPath;
            if (temporaryPath is null)
            {
                throw new InvalidOperationException("No temporary artifact has been opened.");
            }

            var manifestPath = isDecoded ? DecodedManifestPath : OriginalManifestPath;
            var artifact = computedArtifact is null
                ? await ComputeArtifactAsync(temporaryPath, manifestPath, cancellationToken).ConfigureAwait(false)
                : ValidateComputedArtifact(temporaryPath, computedArtifact, manifestPath);

            if (!isDecoded && !_replace && _originalState == ExportArtifactState.PendingExisting && ExistingOriginalArtifact is { } existing)
            {
                // The source hash was unknown when the lease began. The fresh
                // source bytes were read exactly once into the temporary file
                // and hashed; identity is now decided against the existing
                // artifact without re-reading either file. Replace semantics
                // intentionally bypass this decision.
                if (_deferPublish)
                {
                    DeleteTemporary(ref temporaryPath);
                    _originalTemporaryPath = null;
                    if (string.Equals(existing.Sha256, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        _originalState = ExportArtifactState.VerifiedExisting;
                        await NotifyChangedAsync(cancellationToken).ConfigureAwait(false);
                        return existing;
                    }

                    throw new SourceContentMismatchException(
                        "original",
                        existing.ByteLength,
                        artifact.ByteLength,
                        existing.Sha256,
                        artifact.Sha256);
                }

                var directPending = await _store.CommitDirectArtifactAsync(
                    temporaryPath,
                    _finalOriginalPath,
                    artifact,
                    replace: false,
                    pendingExisting: true,
                    cancellationToken).ConfigureAwait(false);
                _originalTemporaryPath = null;
                if (directPending.Existing)
                {
                    _originalState = ExportArtifactState.VerifiedExisting;
                }
                else
                {
                    _originalCommitted = true;
                }

                _committedOriginalArtifact = directPending.Artifact;
                await NotifyChangedAsync(cancellationToken).ConfigureAwait(false);
                return directPending.Artifact;
            }

            var expectedHash = isDecoded ? Record.DecodedSha256 : Record.PayloadSha256;
            var expectedLength = isDecoded ? Record.DecodedByteLength : Record.PayloadByteLength;
            if ((expectedHash is not null && !string.Equals(expectedHash, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
                || (expectedLength is not null && expectedLength.Value != artifact.ByteLength))
            {
                DeleteTemporary(ref temporaryPath);
                if (isDecoded)
                {
                    _decodedTemporaryPath = null;
                }
                else
                {
                    _originalTemporaryPath = null;
                }

                throw new SourceContentMismatchException(isDecoded ? "decoded" : "original", expectedLength, artifact.ByteLength, expectedHash, artifact.Sha256);
            }

            var finalPath = isDecoded ? _decodedPath : _originalPath;
            var directCommit = _deferPublish
                ? new DirectArtifactCommitResult(artifact, Existing: false)
                : await _store.CommitDirectArtifactAsync(
                    temporaryPath,
                    _deferPublish ? finalPath : (isDecoded ? _finalDecodedPath : _finalOriginalPath),
                    artifact,
                    _replace,
                    pendingExisting: false,
                    cancellationToken).ConfigureAwait(false);
            if (_deferPublish)
            {
                // The run transaction owns publication and keeps the root
                // lock for its entire artifact phase.
                AtomicCommit(temporaryPath, finalPath);
            }
            if (isDecoded)
            {
                _decodedTemporaryPath = null;
                _decodedCommitted = !directCommit.Existing;
                _committedDecodedArtifact = directCommit.Artifact;
                if (directCommit.Existing)
                {
                    _decodedState = ExportArtifactState.VerifiedExisting;
                    _existingDecodedArtifact = directCommit.Artifact;
                }
            }
            else
            {
                _originalTemporaryPath = null;
                _originalCommitted = !directCommit.Existing;
                _committedOriginalArtifact = directCommit.Artifact;
                if (directCommit.Existing)
                {
                    _originalState = ExportArtifactState.VerifiedExisting;
                }
                if (_replace
                    && ExistingOriginalArtifact is not null
                    && !string.Equals(ExistingOriginalArtifact.Sha256, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    InvalidateDecodedArtifact();
                }
            }

            await NotifyChangedAsync(cancellationToken).ConfigureAwait(false);
            return artifact;
        }

        private Task NotifyChangedAsync(CancellationToken cancellationToken)
            => _changed is null ? Task.CompletedTask : _changed(this, cancellationToken);

        private static string Relative(string root, string path)
            => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

        private static ExportArtifact ValidateComputedArtifact(string path, ExportArtifact computedArtifact, string relativePath)
        {
            var length = new FileInfo(path).Length;
            if (length != computedArtifact.ByteLength)
            {
                throw new SourceContentMismatchException(
                    "original",
                    computedArtifact.ByteLength,
                    length,
                    computedArtifact.Sha256,
                    "length-mismatch");
            }

            return new ExportArtifact(relativePath, computedArtifact.ByteLength, computedArtifact.Sha256);
        }

        private void InvalidateDecodedArtifact()
        {
            if (_existingDecodedArtifact is null && _decodedState == ExportArtifactState.Missing)
            {
                return;
            }

            if (_deferPublish)
            {
                _invalidateDecodedOnPublish = true;
            }
            else if (File.Exists(_decodedPath))
            {
                File.Delete(_decodedPath);
            }

            _existingDecodedArtifact = null;
            _decodedState = ExportArtifactState.Missing;
        }

        internal static void AtomicCommit(string temporaryPath, string finalPath)
        {
            if (!File.Exists(finalPath))
            {
                File.Move(temporaryPath, finalPath);
                return;
            }

            var backupPath = finalPath + ".backup-" + Guid.NewGuid().ToString("N");
            try
            {
                File.Replace(temporaryPath, finalPath, backupPath, ignoreMetadataErrors: true);
            }
            finally
            {
                try
                {
                    if (File.Exists(backupPath))
                    {
                        File.Delete(backupPath);
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        private static async Task<ExportArtifact> ComputeArtifactAsync(string path, string relativePath, CancellationToken cancellationToken)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[128 * 1024];
            long length = 0;
            int count;
            while ((count = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
            {
                hash.AppendData(buffer, 0, count);
                length = checked(length + count);
            }

            return new ExportArtifact(relativePath, length, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
        }

        private void EnsureUsable()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(FileSystemExportItemLease));
            }
        }

        private static void DeleteTemporary(ref string? path)
        {
            if (path is null)
            {
                return;
            }

            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            path = null;
        }

        private static void DeleteTemporaryPath(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class FileSystemExportRunJournal : IExportRunLease
    {
        private readonly FileSystemVoiceExportStore _store;
        private readonly string _exportRoot;
        private readonly string _runId;
        private readonly string _operationId;
        private readonly string? _selectionFingerprint;
        private readonly string _stagingRoot;
        private readonly string _transactionPath;
        private readonly FileStream _stream;
        private readonly ExportRootLock _rootLock;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly SemaphoreSlim _transactionDocumentGate = new(1, 1);
        private readonly object _transactionStateGate = new();
        private readonly List<FileSystemExportItemLease> _stagedItems = [];
        private TaskCompletionSource<bool> _stagesIdle = CompletedSource(completed: true);
        private int _activeStages;
        private bool _transactionClosing;
        private bool _committed;
        private bool _rolledBack;
        private bool _commitAttempted;
        private bool _disposed;
        private int _disposeStarted;
        private ExportTransactionState _transactionState = ExportTransactionState.Staging;
        private ExportMetadataCommitDescriptor? _metadataCommit;

        public FileSystemExportRunJournal(
            FileSystemVoiceExportStore store,
            string exportRoot,
            VoiceExportRunContext context,
            string operationId,
            FileStream stream,
            ExportRootLock rootLock)
        {
            _store = store;
            _exportRoot = exportRoot;
            _runId = context.RunId;
            _operationId = operationId;
            _selectionFingerprint = context.SelectionFingerprint;
            _stagingRoot = ExportPathSafety.CombineUnderRoot(_exportRoot, "runs", "." + context.RunId + ".staging");
            _transactionPath = ExportPathSafety.CombineUnderRoot(_exportRoot, "runs", context.RunId + ".transaction.json");
            _stream = stream;
            _rootLock = rootLock;
            Directory.CreateDirectory(_stagingRoot);
        }

        public string RunId => _runId;

        internal async Task InitializeAsync(CancellationToken cancellationToken)
            => await PersistTransactionAsync(cancellationToken).ConfigureAwait(false);

        public async ValueTask<IExportItemLease> StageItemAsync(
            VoiceRecord record,
            ExistingArtifactPolicy policy,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(record);
            lock (_transactionStateGate)
            {
                EnsureTransactionUsableLocked();
                if (_activeStages++ == 0)
                {
                    _stagesIdle = CompletedSource();
                }
            }

            IExportItemLease? lease = null;
            var added = false;
            try
            {
                lease = await _store.BeginItemCoreAsync(
                    record,
                    policy,
                    _stagingRoot,
                    deferPublish: true,
                    cancellationToken,
                    OnItemChangedAsync).ConfigureAwait(false);
                lock (_transactionStateGate)
                {
                    if (_transactionClosing || _disposed)
                    {
                        throw new InvalidOperationException("The export run transaction has already completed.");
                    }

                    _stagedItems.Add((FileSystemExportItemLease)lease);
                    added = true;
                }

                await PersistTransactionAsync(cancellationToken).ConfigureAwait(false);

                return lease;
            }
            finally
            {
                if (!added && lease is not null)
                {
                    await lease.DisposeAsync().ConfigureAwait(false);
                }

                lock (_transactionStateGate)
                {
                    if (--_activeStages == 0)
                    {
                        _stagesIdle.TrySetResult(true);
                    }
                }
            }
        }

        public async Task RecordEntryAsync(VoiceExportEntry entry, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(entry);
            lock (_transactionStateGate)
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(FileSystemExportRunJournal));
                }
            }

            await _transactionDocumentGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                lock (_transactionStateGate)
                {
                    var item = _stagedItems.FirstOrDefault(candidate => string.Equals(candidate.Record.MessageId, entry.MessageId, StringComparison.Ordinal));
                    if (item is null)
                    {
                        throw new InvalidDataException("The transaction entry does not have a staged item.");
                    }

                    item.SetEntry(entry);
                }

                await PersistTransactionUnlockedAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _transactionDocumentGate.Release();
            }
        }

        public async Task DiscardItemAsync(string messageId, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
            lock (_transactionStateGate)
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(FileSystemExportRunJournal));
                }

                if (_commitAttempted || _transactionClosing)
                {
                    throw new InvalidOperationException("The export transaction is already committing.");
                }
            }

            await _transactionDocumentGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                FileSystemExportItemLease? removed = null;
                lock (_transactionStateGate)
                {
                    var index = _stagedItems.FindIndex(
                        candidate => string.Equals(candidate.Record.MessageId, messageId, StringComparison.Ordinal));
                    if (index >= 0)
                    {
                        removed = _stagedItems[index];
                        _stagedItems.RemoveAt(index);
                    }
                }

                if (removed is not null)
                {
                    await removed.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    await removed.DisposeAsync().ConfigureAwait(false);
                    await PersistTransactionUnlockedAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                _transactionDocumentGate.Release();
            }
        }

        public async Task CommitAsync(CancellationToken cancellationToken)
        {
            Task idle;
            lock (_transactionStateGate)
            {
                EnsureTransactionUsableLocked();
                _transactionClosing = true;
                idle = _stagesIdle.Task;
            }

            await idle.WaitAsync(cancellationToken).ConfigureAwait(false);
            FileSystemExportItemLease[] items;
            lock (_transactionStateGate)
            {
                items = _stagedItems.ToArray();
            }

            try
            {
                _commitAttempted = true;
                _transactionState = ExportTransactionState.Prepared;
                await PersistTransactionAsync(cancellationToken).ConfigureAwait(false);
                _transactionState = ExportTransactionState.Publishing;
                await PersistTransactionAsync(cancellationToken).ConfigureAwait(false);
                foreach (var item in items.OrderBy(static item => item.OriginalManifestPath, StringComparer.Ordinal))
                {
                    _store.ThrowIfFaultRequested(
                        ExportTransactionFaultPoint.BeforeArtifactPublish,
                        _runId,
                        item.Record.MessageId);
                    await item.PublishStagedAsync(OnItemChangedAsync, cancellationToken).ConfigureAwait(false);
                    _store.ThrowIfFaultRequested(
                        ExportTransactionFaultPoint.AfterArtifactPublish,
                        _runId,
                        item.Record.MessageId);
                    if (item.Entry is { } entry)
                    {
                        _store.ThrowIfFaultRequested(
                            ExportTransactionFaultPoint.BeforeItemJournalCommit,
                            _runId,
                            item.Record.MessageId);
                        await AppendItemCommitEventAsync(entry, cancellationToken).ConfigureAwait(false);
                        _store.ThrowIfFaultRequested(
                            ExportTransactionFaultPoint.AfterItemJournalCommit,
                            _runId,
                            item.Record.MessageId);
                    }
                }

                _transactionState = ExportTransactionState.ArtifactsCommitted;
                await PersistTransactionAsync(cancellationToken).ConfigureAwait(false);
                DeleteStagingDirectory();
                lock (_transactionStateGate)
                {
                    _committed = true;
                }
            }
            catch (Exception exception)
            {
                _transactionState = ExportTransactionState.FailedRecoverable;
                await PersistTransactionAsync(CancellationToken.None).ConfigureAwait(false);
                lock (_transactionStateGate)
                {
                    _commitAttempted = true;
                }

                throw new IOException("The export artifact transaction requires recovery.", exception);
            }
        }

        public async Task RollbackAsync(CancellationToken cancellationToken)
        {
            Task idle;
            lock (_transactionStateGate)
            {
                if (_committed || _rolledBack || _commitAttempted)
                {
                    return;
                }
                if (_transactionClosing)
                {
                    throw new InvalidOperationException("The export run transaction is already completing.");
                }

                _transactionClosing = true;
                idle = _stagesIdle.Task;
            }

            await idle.WaitAsync(cancellationToken).ConfigureAwait(false);
            FileSystemExportItemLease[] items;
            lock (_transactionStateGate)
            {
                items = _stagedItems.ToArray();
            }

            foreach (var item in items)
            {
                await item.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }

            DeleteStagingDirectory();
            lock (_transactionStateGate)
            {
                _stagedItems.Clear();
            }
            _transactionState = ExportTransactionState.RolledBack;
            await PersistTransactionAsync(CancellationToken.None).ConfigureAwait(false);
            lock (_transactionStateGate)
            {
                _rolledBack = true;
            }
        }

        public async Task AppendAsync(VoiceExportJournalEvent journalEvent, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(journalEvent);
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(FileSystemExportRunJournal));
                }

                await AppendCoreAsync(journalEvent, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task FinalizeAsync(VoiceExportManifest manifest, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(manifest);
            if (!string.Equals(manifest.RunId, _runId, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"The manifest RunId '{manifest.RunId}' does not match the active journal RunId '{_runId}'.");
            }

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            var manifestCommitFlushed = false;
            try
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(FileSystemExportRunJournal));
                }

                var journalPath = ExportPathSafety.CombineUnderRoot(_exportRoot, "runs", _runId + ".jsonl");
                var journalManifest = await ReadManifestFromJournalAsync(manifest, journalPath, cancellationToken).ConfigureAwait(false);
                _store.ThrowIfFaultRequested(
                    ExportTransactionFaultPoint.BeforeMetadataCommit,
                    _runId);
                var metadata = await _store.CommitMetadataAsync(journalManifest, _runId, cancellationToken).ConfigureAwait(false);
                _store.ThrowIfFaultRequested(
                    ExportTransactionFaultPoint.AfterMetadataCommit,
                    _runId);
                journalManifest = metadata.Manifest;
                _metadataCommit = metadata.Descriptor;
                _transactionState = ExportTransactionState.MetadataCommitted;
                await PersistTransactionAsync(cancellationToken).ConfigureAwait(false);
                // This is the durable commit marker. Every manifest file is
                // complete before the event is flushed to the Journal.
                await AppendCoreAsync(new VoiceExportJournalEvent(
                    "manifest-committed",
                    _runId,
                    DateTimeOffset.UtcNow,
                    Context: null,
                    ManifestSha256: metadata.Descriptor.PrivateManifestSha256,
                    ManifestGeneratedAtUtc: journalManifest.GeneratedAtUtc), cancellationToken).ConfigureAwait(false);
                _store.ThrowIfFaultRequested(
                    ExportTransactionFaultPoint.AfterManifestCommit,
                    _runId);
                manifestCommitFlushed = true;
                _transactionState = ExportTransactionState.Completed;
                lock (_transactionStateGate)
                {
                    _committed = true;
                }
                try
                {
                    await PersistTransactionAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch when (manifestCommitFlushed)
                {
                    // The flushed Journal marker and descriptor are the
                    // durable commit boundary. A later start can refresh the
                    // transaction document without ever downgrading a
                    // completed run.
                }
            }
            catch (Exception exception)
            {
                if (manifestCommitFlushed)
                {
                    _transactionState = ExportTransactionState.Completed;
                    lock (_transactionStateGate)
                    {
                        _committed = true;
                    }
                    return;
                }

                _transactionState = ExportTransactionState.FailedRecoverable;
                await PersistTransactionAsync(CancellationToken.None).ConfigureAwait(false);
                throw new IOException("The export metadata transaction requires recovery.", exception);
            }
            finally
            {
                _gate.Release();
            }
        }

        private void EnsureTransactionUsableLocked()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(FileSystemExportRunJournal));
            }

            if (_rolledBack || _transactionClosing && !_committed)
            {
                throw new InvalidOperationException("The export run transaction has already completed.");
            }
        }

        private static TaskCompletionSource<bool> CompletedSource(bool completed = false)
        {
            var source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (completed)
            {
                source.TrySetResult(true);
            }

            return source;
        }

        private void DeleteStagingDirectory()
        {
            if (Directory.Exists(_stagingRoot))
            {
                Directory.Delete(_stagingRoot, recursive: true);
            }
        }

        private async Task OnItemChangedAsync(FileSystemExportItemLease item, CancellationToken cancellationToken)
            => await PersistTransactionAsync(cancellationToken).ConfigureAwait(false);

        private async Task AppendItemCommitEventAsync(
            VoiceExportEntry entry,
            CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await AppendCoreAsync(
                    new VoiceExportJournalEvent(
                        entry.WasSkipped ? "item-skipped" : "item-committed",
                        _runId,
                        DateTimeOffset.UtcNow,
                        entry.MessageId,
                        Entry: entry),
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task PersistTransactionAsync(CancellationToken cancellationToken)
        {
            await _transactionDocumentGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await PersistTransactionUnlockedAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _transactionDocumentGate.Release();
            }
        }

        private async Task PersistTransactionUnlockedAsync(CancellationToken cancellationToken)
        {
            FileSystemExportItemLease[] items;
            lock (_transactionStateGate)
            {
                items = _stagedItems.ToArray();
            }

            var document = new ExportTransactionDocument(
                _runId,
                _operationId,
                _selectionFingerprint,
                _transactionState,
                DateTimeOffset.UtcNow,
                items.Select(item => item.CreateTransactionItem(_exportRoot)).ToArray(),
                _metadataCommit,
                _transactionState == ExportTransactionState.FailedRecoverable
                    ? "export-recovery-required"
                    : _transactionState == ExportTransactionState.RolledBack
                        ? "export-rolled-back"
                        : null);
            await AtomicFileWriter.WriteJsonAsync(_transactionPath, document, InfrastructureJson.Indented, cancellationToken).ConfigureAwait(false);
        }

        private async Task AppendCoreAsync(VoiceExportJournalEvent journalEvent, CancellationToken cancellationToken)
        {
            if (!string.Equals(journalEvent.RunId, _runId, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"The journal event RunId '{journalEvent.RunId}' does not match the active journal RunId '{_runId}'.");
            }

            var line = JsonSerializer.Serialize(journalEvent, InfrastructureJson.Compact) + Environment.NewLine;
            var bytes = Encoding.UTF8.GetBytes(line);
            await _stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            _stream.Flush(flushToDisk: true);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            {
                return;
            }

            Task idle;
            var autoRolledBack = false;
            lock (_transactionStateGate)
            {
                if (!_committed && !_rolledBack)
                {
                    _transactionClosing = true;
                }

                idle = _stagesIdle.Task;
            }

            await idle.ConfigureAwait(false);
            lock (_transactionStateGate)
            {
                if (!_committed && !_rolledBack)
                {
                    if (!_commitAttempted)
                    {
                        foreach (var item in _stagedItems) item.RollbackPublished();
                        DeleteStagingDirectory();
                        _stagedItems.Clear();
                        _transactionState = ExportTransactionState.FailedRecoverable;
                        _rolledBack = true;
                        autoRolledBack = true;
                    }
                }

                _disposed = true;
            }

            if (autoRolledBack)
            {
                try
                {
                    await PersistTransactionAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    // Dispose must still release the cross-process lock. The
                    // pre-existing transaction document remains a recoverable
                    // fail-closed record if persistence itself is unavailable.
                }
            }

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await _stream.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
                _gate.Dispose();
                _transactionDocumentGate.Dispose();
                await _rootLock.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
