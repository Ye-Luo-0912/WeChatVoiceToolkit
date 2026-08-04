using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using WeChatVoice.Core.Errors;
using WeChatVoice.Core.Models;
using WeChatVoice.Core.Ports;

namespace WeChatVoice.Application;

/// <summary>
/// Coordinates catalog reads, lease-owned persistence, optional WAV decoding,
/// and an append-only run journal.
/// </summary>
public sealed class VoiceExportService
{
    private const int CopyBufferSize = 81_920;

    private readonly IVoiceCatalog _voiceCatalog;
    private readonly IVoiceExportStore _exportStore;
    private readonly IVoiceDecoder? _voiceDecoder;
    private readonly IVoiceDurationCache? _durationCache;
    private readonly IVoiceDurationResolver? _durationResolver;
    private readonly VoiceExportEligibilityEvaluator _eligibilityEvaluator = new();

    public VoiceExportService(
        IVoiceCatalog voiceCatalog,
        IVoiceExportStore exportStore,
        IVoiceDecoder? voiceDecoder = null,
        IVoiceDurationCache? durationCache = null,
        IVoiceDurationResolver? durationResolver = null)
    {
        _voiceCatalog = voiceCatalog ?? throw new ArgumentNullException(nameof(voiceCatalog));
        _exportStore = exportStore ?? throw new ArgumentNullException(nameof(exportStore));
        _voiceDecoder = voiceDecoder;
        _durationCache = durationCache;
        _durationResolver = durationResolver;
    }

    public Task<VoiceExportManifest> ExportAsync(VoiceQuery query, CancellationToken cancellationToken = default)
        => ExportAsync(query, options: null, cancellationToken);

    public async Task<VoiceExportManifest> ExportAsync(
        VoiceQuery query,
        VoiceExportOptions? options,
        CancellationToken cancellationToken = default)
        => await ExportCoreAsync(
            query,
            options,
            preparedSelectionFactory: null,
            validatePreparedSelectionBeforeExport: true,
            cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Exports the exact records captured by a preceding metadata scan. No
    /// catalog voice query is issued here; only eligible payload locators are
    /// opened while staging artifacts.
    /// </summary>
    public async Task<VoiceExportManifest> ExportPreparedAsync(
        VoiceQuery query,
        VoiceExportOptions? options,
        IReadOnlyList<VoiceRecord> preparedSelection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preparedSelection);
        return await ExportCoreAsync(
            query,
            options,
            cancellationTokenValue => EnumeratePreparedAsync(preparedSelection, cancellationTokenValue),
            validatePreparedSelectionBeforeExport: true,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Exports a disk-backed prepared selection. The spool is read twice at
    /// most (integrity/selection validation and payload export), but the
    /// verified catalog is never queried again and VoiceRecord metadata is not
    /// materialized as a second large list.
    /// </summary>
    public async Task<VoiceExportManifest> ExportPreparedSpoolAsync(
        VoiceQuery query,
        VoiceExportOptions? options,
        PreparedSelectionSpoolDescriptor spool,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spool);
        await using var lease = await PreparedSelectionSpool.OpenLeaseAsync(spool, cancellationToken).ConfigureAwait(false);
        return await ExportCoreAsync(
            query,
            options,
            cancellationTokenValue => lease.ReadOnceAsync(cancellationTokenValue),
            validatePreparedSelectionBeforeExport: false,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<VoiceExportManifest> ExportCoreAsync(
        VoiceQuery query,
        VoiceExportOptions? options,
        Func<CancellationToken, IAsyncEnumerable<VoiceRecord>>? preparedSelectionFactory,
        bool validatePreparedSelectionBeforeExport,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        options ??= new VoiceExportOptions();
        if (options.MaxDegreeOfParallelism <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxDegreeOfParallelism must be greater than zero.");
        }

        // A compatibility export may still build its immutable list here. A
        // formal guided export passes a list or spool captured by
        // VoiceScanWorkflow, so this branch performs no second metadata query.
        if (preparedSelectionFactory is null && HasExpectedSelection(options))
        {
            var preparedSelection = await PrepareExpectedSelectionAsync(query, options, cancellationToken).ConfigureAwait(false);
            preparedSelectionFactory = cancellationTokenValue => EnumeratePreparedAsync(preparedSelection, cancellationTokenValue);
        }

        if (preparedSelectionFactory is not null
            && validatePreparedSelectionBeforeExport
            && HasExpectedSelection(options))
        {
            await ValidateExpectedSelectionAsync(preparedSelectionFactory(cancellationToken), query, options, cancellationToken).ConfigureAwait(false);
        }

        var context = _voiceCatalog.Context;
        // Export performs one streaming read of each source BLOB and computes
        // its identity at commit time; a DeepScan pre-hash is deliberately not
        // forced here so the source is never read twice.
        var runId = Guid.NewGuid().ToString("N");
        await using var journal = await _exportStore.BeginRunAsync(
            new VoiceExportRunContext(runId, context, DateTimeOffset.UtcNow, options.ExpectedResultSetFingerprint),
            cancellationToken).ConfigureAwait(false);
        var transaction = (IExportRunTransaction)journal;
        await AppendAsync(journal, new VoiceExportJournalEvent("run-started", runId, DateTimeOffset.UtcNow, Context: context), cancellationToken).ConfigureAwait(false);

        var entries = new ConcurrentQueue<VoiceExportEntry>();
        var failures = new ConcurrentQueue<VoiceExportFailure>();
        var activeExports = new List<Task<bool>>(options.MaxDegreeOfParallelism);
        var cancellationObserved = false;
        var runFailed = false;
        var commitFailed = false;
        AppFailureException? selectionMismatch = null;
        var preparedSelectionCompleted = false;
        using var preparedFingerprint = preparedSelectionFactory is not null && HasExpectedSelection(options)
            ? new VoiceResultSetFingerprintBuilder()
            : null;
        try
        {
            var records = preparedSelectionFactory is null
                ? VoiceSelectionEnumerator.EnumerateAsync(
                    _voiceCatalog,
                    query,
                    _durationResolver,
                    bypassCatalogDeepScan: false,
                    cancellationToken: cancellationToken)
                : preparedSelectionFactory(cancellationToken);
            await foreach (var record in records.ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (preparedFingerprint is not null
                    && _eligibilityEvaluator.Evaluate(record, context, query).IsEligible)
                {
                    preparedFingerprint.Append(record);
                }

                if (activeExports.Count >= options.MaxDegreeOfParallelism)
                {
                    if (!await DrainOneAsync(activeExports, cancellationToken).ConfigureAwait(false)
                        && options.CompletionPolicy == ExportCompletionPolicy.ExactAllOrNothing)
                    {
                        runFailed = true;
                    }
                }

                activeExports.Add(ExportOneAsync(record, query, options, context, runId, transaction, journal, entries, failures, cancellationToken));
            }

            preparedSelectionCompleted = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancellationObserved = true;
        }
        catch (AppFailureException exception) when (exception.Code == ErrorCode.SelectionPlanMismatch)
        {
            runFailed = true;
            selectionMismatch = exception;
            var failure = CreateFailure(null, "selection", exception);
            failures.Enqueue(failure);
            await AppendAsync(journal, new VoiceExportJournalEvent("run-failed", runId, DateTimeOffset.UtcNow, Context: context, Failure: failure), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            runFailed = true;
            var failure = CreateFailure(null, "query", exception);
            failures.Enqueue(failure);
            await AppendAsync(journal, new VoiceExportJournalEvent("item-failed", runId, DateTimeOffset.UtcNow, Failure: failure), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                var completed = await Task.WhenAll(activeExports).ConfigureAwait(false);
                if (options.CompletionPolicy == ExportCompletionPolicy.ExactAllOrNothing
                    && completed.Any(static succeeded => !succeeded))
                {
                    runFailed = true;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                cancellationObserved = true;
            }
            catch (Exception exception)
            {
                runFailed = true;
                failures.Enqueue(CreateFailure(null, "export", exception));
            }
        }

        if (preparedFingerprint is not null && preparedSelectionCompleted)
        {
            try
            {
                var actualFingerprint = preparedFingerprint.Complete();
                if ((options.ExpectedResultSetFingerprint is not null
                        && !string.Equals(actualFingerprint, options.ExpectedResultSetFingerprint, StringComparison.OrdinalIgnoreCase))
                    || (options.ExpectedResultCount is not null && preparedFingerprint.Count != options.ExpectedResultCount.Value)
                    || (options.ExpectedTotalPayloadBytes is not null && preparedFingerprint.TotalPayloadBytes != options.ExpectedTotalPayloadBytes.Value))
                {
                    selectionMismatch = new AppFailureException(
                        ErrorCode.SelectionPlanMismatch,
                        "The prepared voice selection changed during export; no artifacts were committed.");
                    runFailed = true;
                    var failure = CreateFailure(null, "selection", selectionMismatch);
                    failures.Enqueue(failure);
                    await AppendAsync(journal, new VoiceExportJournalEvent("run-failed", runId, DateTimeOffset.UtcNow, Context: context, Failure: failure), CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (InvalidDataException exception)
            {
                selectionMismatch = new AppFailureException(
                    ErrorCode.SelectionPlanMismatch,
                    "The prepared voice selection lacks a stable identity.",
                    exception);
                runFailed = true;
                failures.Enqueue(CreateFailure(null, "selection", selectionMismatch));
            }
        }

        if (cancellationObserved || runFailed || cancellationToken.IsCancellationRequested)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        else
        {
            try
            {
                await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                commitFailed = true;
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                runFailed = true;
                failures.Enqueue(CreateFailure(null, "commit", exception));
                await AppendAsync(
                    journal,
                    new VoiceExportJournalEvent(
                        "run-failed",
                        runId,
                        DateTimeOffset.UtcNow,
                        Context: context,
                        Failure: CreateFailure(null, "commit", exception)),
                    CancellationToken.None).ConfigureAwait(false);
            }
        }

        var runStatus = cancellationObserved || cancellationToken.IsCancellationRequested
            ? ExportRunStatus.Cancelled
            : runFailed
                ? ExportRunStatus.Failed
                : failures.Count > 0
                    ? ExportRunStatus.CompletedWithFailures
                    : ExportRunStatus.Completed;
        var manifestEntries = runFailed && options.CompletionPolicy == ExportCompletionPolicy.ExactAllOrNothing
            ? Array.Empty<VoiceExportEntry>()
            : entries.OrderBy(static entry => entry.OccurredAtUtc).ThenBy(static entry => entry.MessageId, StringComparer.Ordinal).ToArray();
        var manifest = new VoiceExportManifest(
            DateTimeOffset.UtcNow,
            manifestEntries,
            failures.OrderBy(static failure => failure.MessageId ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static failure => failure.Stage, StringComparer.Ordinal)
                .ThenBy(static failure => failure.Error, StringComparer.Ordinal).ToArray(),
            runId,
            context.SnapshotId,
            context.AdapterId,
            context.AccountId,
            context.DatasetId,
            context.AdapterVersion,
            context.DatabaseFingerprints,
            runStatus,
            runStatus == ExportRunStatus.Cancelled,
            context.MaterializationProvenance,
            context.AccountIdentity);

        await AppendAsync(
            journal,
            new VoiceExportJournalEvent(
                runStatus == ExportRunStatus.Cancelled
                    ? "run-cancelled"
                    : runStatus == ExportRunStatus.Failed
                        ? "run-failed"
                        : "processing-completed",
                runId,
                DateTimeOffset.UtcNow,
                Context: context,
                Cancelled: runStatus == ExportRunStatus.Cancelled),
            CancellationToken.None).ConfigureAwait(false);
        if (!commitFailed && (!runFailed || options.CompletionPolicy == ExportCompletionPolicy.BestEffort))
        {
            await journal.FinalizeAsync(manifest, CancellationToken.None).ConfigureAwait(false);
        }
        if (cancellationObserved || cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        if (selectionMismatch is not null)
        {
            throw selectionMismatch;
        }

        return manifest;
    }

    private async Task<IReadOnlyList<VoiceRecord>> PrepareExpectedSelectionAsync(
        VoiceQuery query,
        VoiceExportOptions options,
        CancellationToken cancellationToken)
    {
        var prepared = new List<VoiceRecord>();
        await foreach (var record in VoiceSelectionEnumerator.EnumerateAsync(
            _voiceCatalog,
            query,
            _durationResolver,
            bypassCatalogDeepScan: false,
            cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            prepared.Add(record);
        }

        await ValidateExpectedSelectionAsync(
            EnumeratePreparedAsync(prepared, cancellationToken),
            query,
            options,
            cancellationToken).ConfigureAwait(false);
        return prepared;
    }

    private async Task ValidateExpectedSelectionAsync(
        IAsyncEnumerable<VoiceRecord> prepared,
        VoiceQuery query,
        VoiceExportOptions options,
        CancellationToken cancellationToken)
    {
        using var fingerprint = new VoiceResultSetFingerprintBuilder();
        await foreach (var record in prepared.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (_eligibilityEvaluator.Evaluate(record, _voiceCatalog.Context, query).IsEligible)
            {
                fingerprint.Append(record);
            }
        }

        var actual = fingerprint.Complete();
        if ((options.ExpectedResultSetFingerprint is not null
                && !string.Equals(actual, options.ExpectedResultSetFingerprint, StringComparison.OrdinalIgnoreCase))
            || (options.ExpectedResultCount is not null && fingerprint.Count != options.ExpectedResultCount.Value)
            || (options.ExpectedTotalPayloadBytes is not null && fingerprint.TotalPayloadBytes != options.ExpectedTotalPayloadBytes.Value))
        {
            throw new AppFailureException(
                ErrorCode.SelectionPlanMismatch,
                "The voice result set changed before export; export was not started.");
        }
    }

    private static async IAsyncEnumerable<VoiceRecord> EnumeratePreparedAsync(
        IReadOnlyList<VoiceRecord> prepared,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var record in prepared)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return record;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task<bool> DrainOneAsync(List<Task<bool>> activeExports, CancellationToken cancellationToken)
    {
        var completed = await Task.WhenAny(activeExports).ConfigureAwait(false);
        activeExports.Remove(completed);
        try
        {
            return await completed.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task<bool> ExportOneAsync(
        VoiceRecord record,
        VoiceQuery query,
        VoiceExportOptions options,
        VoiceCatalogContext context,
        string runId,
        IExportRunTransaction transaction,
        IExportRunLease journal,
        ConcurrentQueue<VoiceExportEntry> entries,
        ConcurrentQueue<VoiceExportFailure> failures,
        CancellationToken cancellationToken)
    {
        IExportItemLease? lease = null;
        var entryRecorded = false;
        try
        {
            var contextError = ValidateContext(record, context);
            if (contextError is not null)
            {
                await RecordFailureAsync(record, "context", contextError, runId, journal, failures, cancellationToken).ConfigureAwait(false);
                return false;
            }

            var eligibility = _eligibilityEvaluator.Evaluate(record, context, query);
            if (!eligibility.IsEligible)
            {
                var stage = record.PayloadState switch
                {
                    VoicePayloadState.Empty => "payload-empty",
                    VoicePayloadState.InvalidHeader => "payload-invalid-header",
                    VoicePayloadState.Ambiguous => "payload-ambiguous",
                    VoicePayloadState.Missing => "association",
                    _ => eligibility.ReasonCode ?? "eligibility",
                };
                await RecordFailureAsync(record, stage, eligibility.Detail ?? "The voice record is not exportable.", runId, journal, failures, cancellationToken).ConfigureAwait(false);
                return false;
            }

            if (record.SourceStableKey is null)
            {
                await RecordFailureAsync(record, "identity", "The voice record lacks a complete SourceStableKey; reusable export is refused.", runId, journal, failures, cancellationToken).ConfigureAwait(false);
                return false;
            }

            lease = await transaction.StageItemAsync(record, ExistingArtifactPolicy.SkipIfHashMatches, cancellationToken).ConfigureAwait(false);
            if (lease.OriginalState == ExportArtifactState.Conflict)
            {
                await RecordFailureAsync(record, "source-content-mismatch", "The existing original artifact conflicts with the source expectation.", runId, journal, failures, cancellationToken).ConfigureAwait(false);
                return false;
            }

            var originalArtifact = lease.OriginalState == ExportArtifactState.VerifiedExisting
                ? lease.ExistingOriginalArtifact!
                : await CopyOriginalAsync(record, lease, cancellationToken).ConfigureAwait(false);
            var durationMs = await TryReadCachedDurationAsync(record, originalArtifact.Sha256, cancellationToken).ConfigureAwait(false)
                ?? record.DurationMs;
            ExportArtifact? decodedArtifact = lease.DecodedState == ExportArtifactState.VerifiedExisting
                ? lease.ExistingDecodedArtifact
                : null;
            var hasDecodeError = false;
            var qualityFlags = new List<string>();

            if (options.DecodeToWav && decodedArtifact is null)
            {
                if (_voiceDecoder is null)
                {
                    hasDecodeError = true;
                    qualityFlags.Add("decoder-not-configured");
                    await RecordFailureAsync(record, "decode", "WAV decoding was requested but no voice decoder was configured.", runId, journal, failures, cancellationToken).ConfigureAwait(false);
                    if (options.CompletionPolicy == ExportCompletionPolicy.ExactAllOrNothing)
                    {
                        return false;
                    }
                }
                else if (lease.DecodedState == ExportArtifactState.Conflict)
                {
                    hasDecodeError = true;
                    qualityFlags.Add("existing-decoded-conflict");
                    await RecordFailureAsync(record, "decode-existing", "The existing decoded artifact conflicts with the expected decoded content.", runId, journal, failures, cancellationToken).ConfigureAwait(false);
                    if (options.CompletionPolicy == ExportCompletionPolicy.ExactAllOrNothing)
                    {
                        return false;
                    }
                }
                else
                {
                    try
                    {
                        await using (var input = await lease.OpenOriginalReadAsync(cancellationToken).ConfigureAwait(false))
                        await using (var output = await lease.OpenDecodedWriteAsync(cancellationToken).ConfigureAwait(false))
                        {
                            await _voiceDecoder.DecodeAsync(input, output, cancellationToken).ConfigureAwait(false);
                            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                        }

                        decodedArtifact = await lease.CommitDecodedAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (SourceContentMismatchException exception)
                    {
                        hasDecodeError = true;
                        qualityFlags.Add("source-content-mismatch");
                        await RecordFailureAsync(record, "source-content-mismatch", exception.Message, runId, journal, failures, cancellationToken).ConfigureAwait(false);
                        if (options.CompletionPolicy == ExportCompletionPolicy.ExactAllOrNothing)
                        {
                            return false;
                        }
                    }
                    catch (Exception exception)
                    {
                        hasDecodeError = true;
                        qualityFlags.Add("decode-error");
                        await RecordFailureAsync(record, "decode", exception.Message, runId, journal, failures, cancellationToken).ConfigureAwait(false);
                        if (options.CompletionPolicy == ExportCompletionPolicy.ExactAllOrNothing)
                        {
                            return false;
                        }
                    }
                }
            }

            var entry = CreateEntry(record, originalArtifact, decodedArtifact, durationMs, hasDecodeError, qualityFlags, lease.OriginalState == ExportArtifactState.VerifiedExisting && (!options.DecodeToWav || lease.DecodedState == ExportArtifactState.VerifiedExisting));
            await transaction.RecordEntryAsync(entry, cancellationToken).ConfigureAwait(false);
            entryRecorded = true;
            // Keep the in-memory manifest projection only after its durable
            // transaction binding has succeeded.
            entries.Enqueue(entry);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SourceContentMismatchException exception)
        {
            await RecordFailureAsync(record, "source-content-mismatch", exception.Message, runId, journal, failures, cancellationToken).ConfigureAwait(false);
            return false;
        }
        catch (Exception exception)
        {
            await RecordFailureAsync(record, "export", exception.Message, runId, journal, failures, cancellationToken).ConfigureAwait(false);
            return false;
        }
        finally
        {
            if (lease is not null)
            {
                try
                {
                    await lease.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    await RecordFailureAsync(record, "rollback", exception.Message, runId, journal, failures, CancellationToken.None).ConfigureAwait(false);
                }

                await lease.DisposeAsync().ConfigureAwait(false);
            }

            if (!entryRecorded)
            {
                try
                {
                    await transaction.DiscardItemAsync(
                        ExportItemTransactionKey.Compute(record.SourceStableKey!),
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    await RecordFailureAsync(record, "transaction", exception.Message, runId, journal, failures, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task RecordFailureAsync(
        VoiceRecord record,
        string stage,
        string error,
        string runId,
        IExportRunLease journal,
        ConcurrentQueue<VoiceExportFailure> failures,
        CancellationToken cancellationToken)
    {
        // Failure records are persisted in manifests and journals. Exception
        // text may contain local paths, account identifiers, or SQLite data.
        var failure = new VoiceExportFailure(record.MessageId, stage, stage);
        failures.Enqueue(failure);
        await AppendAsync(journal, new VoiceExportJournalEvent("item-failed", runId, DateTimeOffset.UtcNow, record.MessageId, Failure: failure), cancellationToken).ConfigureAwait(false);
    }

    private async Task<ExportArtifact> CopyOriginalAsync(VoiceRecord record, IExportItemLease lease, CancellationToken cancellationToken)
    {
        await using var input = await _voiceCatalog.OpenPayloadAsync(record.PayloadLocator!, cancellationToken).ConfigureAwait(false);
        if (!input.CanRead)
        {
            throw new InvalidOperationException("The voice catalog returned a non-readable payload stream.");
        }

        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long length = 0;
        try
        {
            await using (var output = await lease.OpenOriginalWriteAsync(cancellationToken).ConfigureAwait(false))
            {
                while (true)
                {
                    var count = await input.ReadAsync(buffer.AsMemory(0, CopyBufferSize), cancellationToken).ConfigureAwait(false);
                    if (count == 0)
                    {
                        break;
                    }

                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, count);
                    length = checked(length + count);
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            var artifact = new ExportArtifact(
                lease.OriginalManifestPath,
                length,
                Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
            return await lease.CommitOriginalAsync(artifact, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static string? ValidateContext(VoiceRecord record, VoiceCatalogContext context)
    {
        if (record.DataSetId is not null && !string.Equals(record.DataSetId, context.DatasetId, StringComparison.Ordinal))
        {
            return "Voice record DatasetId differs from the catalog context.";
        }

        if (record.AdapterId is not null && !string.Equals(record.AdapterId, context.AdapterId, StringComparison.Ordinal))
        {
            return "Voice record AdapterId differs from the catalog context.";
        }

        if (record.AdapterFamily is not null && !string.Equals(record.AdapterFamily, context.AdapterFamily, StringComparison.Ordinal))
        {
            return "Voice record adapter family differs from the catalog context.";
        }

        if (record.AccountStableId is not null && !string.Equals(record.AccountStableId, context.AccountId, StringComparison.Ordinal))
        {
            return "Voice record AccountId differs from the catalog context.";
        }

        if (record.SnapshotId is not null && !string.Equals(record.SnapshotId, context.SnapshotId, StringComparison.Ordinal))
        {
            return "Voice record SnapshotId differs from the catalog context.";
        }

        if (record.AdapterVersion is not null && !string.Equals(record.AdapterVersion, context.AdapterVersion, StringComparison.Ordinal))
        {
            return "Voice record AdapterVersion differs from the catalog context.";
        }

        if (record.DatabaseFingerprints.Count > 0
            && !record.DatabaseFingerprints.SequenceEqual(context.DatabaseFingerprints, StringComparer.OrdinalIgnoreCase))
        {
            return "Voice record database fingerprints differ from the catalog context.";
        }

        return null;
    }

    private static VoiceExportFailure CreateFailure(string? messageId, string stage, Exception exception)
        => new(messageId, stage, stage);

    private static VoiceExportEntry CreateEntry(
        VoiceRecord record,
        ExportArtifact originalArtifact,
        ExportArtifact? decodedArtifact,
        long? durationMs,
        bool hasDecodeError,
        IReadOnlyList<string> qualityFlags,
        bool wasSkipped)
        => new(
            record.MessageId,
            record.ConversationId,
            record.OccurredAtUtc,
            record.Direction,
            originalArtifact.RelativePath,
            originalArtifact.ByteLength,
            originalArtifact.Sha256,
            decodedArtifact?.RelativePath,
            record.SourceStableKey,
            wasSkipped,
            record.SourceDatabase,
            record.ShardId,
            durationMs,
            originalArtifact.Sha256,
            decodedArtifact?.Sha256,
            record.SpeakerId,
            hasDecodeError,
            qualityFlags.Count == 0 && durationMs is not null ? Array.Empty<string>() : qualityFlags.Concat(durationMs is null ? ["duration-unknown"] : Array.Empty<string>()).ToArray(),
            false,
            wasSkipped ? ExportState.VerifiedExisting : ExportState.Exported,
            TrainingEligibility.Unknown,
            UserSelectionState.NotSelected);

    private async Task<long?> TryReadCachedDurationAsync(
        VoiceRecord record,
        string payloadSha256,
        CancellationToken cancellationToken)
    {
        if (_durationCache is null || record.SourceStableKey is null || string.IsNullOrWhiteSpace(payloadSha256))
        {
            return null;
        }

        try
        {
            return await _durationCache.TryGetAsync(
                new VoiceDurationCacheKey(record.SourceStableKey, payloadSha256, _durationCache.DecoderVersion),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static Task AppendAsync(IExportRunLease journal, VoiceExportJournalEvent journalEvent, CancellationToken cancellationToken)
        => journal.AppendAsync(journalEvent, cancellationToken);

    private static bool HasExpectedSelection(VoiceExportOptions options)
        => options.ExpectedResultSetFingerprint is not null
            || options.ExpectedResultCount is not null
            || options.ExpectedTotalPayloadBytes is not null;
}
