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

    public VoiceScanService(IVoiceCatalog catalog, IVoiceDurationResolver? durationResolver = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _durationResolver = durationResolver;
    }

    public async Task<VoiceScanReport> ScanAsync(VoiceQuery query, CancellationToken cancellationToken = default)
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
        using var resultSetFingerprint = new VoiceResultSetFingerprintBuilder();

        await foreach (var record in _catalog.QueryVoicesAsync(query, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            resultSetFingerprint.Append(record);
            count++;
            if (record.DurationMs is > 0)
            {
                duration = checked(duration + record.DurationMs.Value);
                durationKnown++;
            }
            else if (query.ResolveDuration && _durationResolver is not null && record.PayloadState == VoicePayloadState.Linked)
            {
                var resolvedDuration = await _durationResolver.ResolveAsync(_catalog, record, cancellationToken).ConfigureAwait(false);
                if (resolvedDuration is > 0)
                {
                    duration = checked(duration + resolvedDuration.Value);
                    durationKnown++;
                }
            }

            earliest = earliest is null || record.OccurredAtUtc < earliest ? record.OccurredAtUtc : earliest;
            latest = latest is null || record.OccurredAtUtc > latest ? record.OccurredAtUtc : latest;
            var shard = record.ShardId ?? record.SourceDatabase ?? "unknown";
            shardCounts[shard] = shardCounts.TryGetValue(shard, out var shardCount) ? shardCount + 1 : 1;
            var stateName = record.PayloadState.ToString();
            payloadStates[stateName] = payloadStates.TryGetValue(stateName, out var stateCount) ? stateCount + 1 : 1;
            if (record.PayloadState == VoicePayloadState.Linked && record.PayloadByteLength is > 0)
            {
                exportable++;
                totalPayloadBytes = checked(totalPayloadBytes + record.PayloadByteLength.Value);
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

            if (query.DeepScan && !string.IsNullOrWhiteSpace(record.PayloadSha256))
            {
                payloadHashes[record.PayloadSha256] = payloadHashes.TryGetValue(record.PayloadSha256, out var hashCount) ? hashCount + 1 : 1;
            }
        }

        var duplicates = payloadHashes.Values.Where(static value => value > 1).Sum(static value => value - 1);
        return new VoiceScanReport(
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
    }
}
