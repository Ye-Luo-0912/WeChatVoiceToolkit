using WeChatVoice.Core.Errors;
using WeChatVoice.Core.Models;
using WeChatVoice.Core.Ports;

namespace WeChatVoice.Application;

/// <summary>
/// One authoritative post-query filter and limit pipeline shared by Scan and
/// Export. Catalogs may push SQL filters down, but this layer owns duration and
/// payload-size semantics and applies MaximumResults after those filters.
/// </summary>
public static class VoiceSelectionEnumerator
{
    public static async IAsyncEnumerable<VoiceRecord> EnumerateAsync(
        IVoiceCatalog catalog,
        VoiceQuery query,
        IVoiceDurationResolver? durationResolver,
        bool bypassCatalogDeepScan,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(query);
        if ((query.MinimumDurationMs is not null || query.MaximumDurationMs is not null)
            && (!query.ResolveDuration || durationResolver is null))
        {
            throw new AppFailureException(
                ErrorCode.InvalidRequest,
                "Duration filters require an available duration resolver.");
        }

        var catalogQuery = query.RequiresPostQueryFiltering
            ? query.WithMaximumResults(null)
            : query;
        if (bypassCatalogDeepScan && catalogQuery.DeepScan)
        {
            catalogQuery = catalogQuery.WithDeepScan(false);
        }
        var selected = 0;
        await foreach (var record in catalog.QueryVoicesAsync(catalogQuery, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            var enriched = record;
            if (query.ResolveDuration
                && record.DurationMs is null
                && record.PayloadState == VoicePayloadState.Linked
                && durationResolver is not null)
            {
                var duration = await durationResolver.ResolveAsync(catalog, record, cancellationToken).ConfigureAwait(false);
                enriched = record.WithDuration(duration);
            }

            if (!Matches(query, enriched))
            {
                continue;
            }

            yield return enriched;
            selected++;
            if (query.MaximumResults is not null && selected >= query.MaximumResults.Value)
            {
                yield break;
            }
        }
    }

    private static bool Matches(VoiceQuery query, VoiceRecord record)
    {
        if (query.MinimumPayloadBytes is not null
            && (record.PayloadByteLength is null || record.PayloadByteLength.Value < query.MinimumPayloadBytes.Value))
        {
            return false;
        }

        if (query.MaximumPayloadBytes is not null
            && (record.PayloadByteLength is null || record.PayloadByteLength.Value > query.MaximumPayloadBytes.Value))
        {
            return false;
        }

        if (query.MinimumDurationMs is not null
            && (record.DurationMs is null || record.DurationMs.Value < query.MinimumDurationMs.Value))
        {
            return false;
        }

        if (query.MaximumDurationMs is not null
            && (record.DurationMs is null || record.DurationMs.Value > query.MaximumDurationMs.Value))
        {
            return false;
        }

        return true;
    }
}
