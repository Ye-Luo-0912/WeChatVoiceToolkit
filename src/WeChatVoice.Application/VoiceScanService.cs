using WeChatVoice.Core.Models;
using WeChatVoice.Core.Ports;

namespace WeChatVoice.Application;

/// <summary>
/// Audits voice metadata without opening payload output files.
/// </summary>
public sealed class VoiceScanService
{
    private readonly IVoiceCatalog _catalog;
    private readonly IVoiceDurationResolver? _durationResolver;
    private readonly CachedVoicePayloadHashResolver? _payloadHashResolver;

    public VoiceScanService(
        IVoiceCatalog catalog,
        IVoiceDurationResolver? durationResolver = null,
        IVoicePayloadHashCache? payloadHashCache = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _durationResolver = durationResolver;
        _payloadHashResolver = payloadHashCache is null ? null : new CachedVoicePayloadHashResolver(payloadHashCache);
    }

    public async Task<VoiceScanReport> ScanAsync(VoiceQuery query, CancellationToken cancellationToken = default)
        => (await ScanWithRecordsAsync(query, cancellationToken).ConfigureAwait(false)).Report;

    /// <summary>
    /// Executes the authoritative metadata selection once and returns both the
    /// report and the exact immutable record list that produced it. Formal
    /// exports use this list instead of querying the catalog a second time.
    /// </summary>
    public async Task<VoiceScanExecutionResult> ScanWithRecordsAsync(
        VoiceQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var shardCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var payloadHashes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var payloadStates = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var count = 0;
        long duration = 0;
        DateTimeOffset? earliest = null;
        DateTimeOffset? latest = null;
        var unassociated = 0;
        var empty = 0;
        var invalidHeader = 0;
        var ambiguous = 0;
        var exportable = 0;
        long totalPayloadBytes = 0;
        var durationKnown = 0;
        var records = new List<VoiceRecord>();
        using var resultSetFingerprint = new VoiceResultSetFingerprintBuilder();
        var eligibilityEvaluator = new VoiceExportEligibilityEvaluator();

        await foreach (var record in VoiceSelectionEnumerator.EnumerateAsync(
            _catalog,
            query,
            _durationResolver,
            bypassCatalogDeepScan: _payloadHashResolver is not null,
            cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            records.Add(record);
            count++;
            if (record.DurationMs is > 0)
            {
                duration = checked(duration + record.DurationMs.Value);
                durationKnown++;
            }
            earliest = earliest is null || record.OccurredAtUtc < earliest ? record.OccurredAtUtc : earliest;
            latest = latest is null || record.OccurredAtUtc > latest ? record.OccurredAtUtc : latest;
            var shard = record.ShardId ?? record.SourceDatabase ?? "unknown";
            shardCounts[shard] = shardCounts.TryGetValue(shard, out var shardCount) ? shardCount + 1 : 1;
            var stateName = record.PayloadState.ToString();
            payloadStates[stateName] = payloadStates.TryGetValue(stateName, out var stateCount) ? stateCount + 1 : 1;
            var eligibility = eligibilityEvaluator.Evaluate(record, _catalog.Context, query);
            if (eligibility.IsEligible)
            {
                resultSetFingerprint.Append(record);
                exportable++;
                totalPayloadBytes = checked(totalPayloadBytes + (record.PayloadByteLength ?? 0));
            }
            if (record.PayloadState == VoicePayloadState.Missing)
            {
                unassociated++;
            }

            if (record.PayloadState == VoicePayloadState.Empty)
            {
                empty++;
            }

            if (record.PayloadState == VoicePayloadState.InvalidHeader)
            {
                invalidHeader++;
            }

            if (record.PayloadState == VoicePayloadState.Ambiguous)
            {
                ambiguous++;
            }

            var payloadHash = record.PayloadSha256;
            if (query.DeepScan
                && string.IsNullOrWhiteSpace(payloadHash)
                && _payloadHashResolver is not null)
            {
                payloadHash = await _payloadHashResolver.ResolveAsync(_catalog, record, cancellationToken).ConfigureAwait(false);
            }

            if (query.DeepScan && !string.IsNullOrWhiteSpace(payloadHash))
            {
                payloadHashes[payloadHash] = payloadHashes.TryGetValue(payloadHash, out var hashCount) ? hashCount + 1 : 1;
            }
        }

        var duplicates = payloadHashes.Values.Where(static value => value > 1).Sum(static value => value - 1);
        var report = new VoiceScanReport(
            count,
            duration,
            earliest,
            latest,
            shardCounts,
            unassociated,
            empty,
            duplicates,
            invalidHeader,
            ambiguous,
            payloadStates,
            query.DeepScan,
            exportable,
            totalPayloadBytes,
            durationKnown,
            resultSetFingerprint.Complete());
        return new VoiceScanExecutionResult(
            report,
            new System.Collections.ObjectModel.ReadOnlyCollection<VoiceRecord>(records));
    }
}

public sealed record VoiceScanExecutionResult(
    VoiceScanReport Report,
    IReadOnlyList<VoiceRecord> Records);
