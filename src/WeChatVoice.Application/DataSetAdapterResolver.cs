using WeChatVoice.Core.Models;
using WeChatVoice.Core.Ports;

namespace WeChatVoice.Application;

/// <summary>
/// Selects the highest-confidence verified adapter for a complete data set.
/// Ties are rejected so adapter registration cannot silently change meaning.
/// </summary>
public sealed class DataSetAdapterResolver : IWeChatDataSetAdapterResolver
{
    private readonly IReadOnlyList<IWeChatDataSetAdapter> _adapters;

    public DataSetAdapterResolver(IEnumerable<IWeChatDataSetAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        _adapters = adapters.ToArray();
    }

    public IWeChatDataSetAdapter Resolve(WeChatDataSet dataSet)
    {
        ArgumentNullException.ThrowIfNull(dataSet);
        var matches = _adapters
            .Select(adapter => (Adapter: adapter, Match: adapter.Probe(dataSet)))
            .Where(static item => item.Match.IsMatch)
            .OrderByDescending(static item => item.Match.Score)
            .ToArray();

        if (matches.Length == 0)
        {
            throw new NoMatchingDataSetAdapterException(dataSet.DataSetId);
        }

        if (matches.Length > 1 && matches[0].Match.Score == matches[1].Match.Score)
        {
            throw new InvalidOperationException(
                $"Multiple WeChat data-set adapters matched '{dataSet.DataSetId}' with the same score: " +
                string.Join(", ", matches.Where(item => item.Match.Score == matches[0].Match.Score).Select(item => item.Adapter.Id)));
        }

        return matches[0].Adapter;
    }
}
