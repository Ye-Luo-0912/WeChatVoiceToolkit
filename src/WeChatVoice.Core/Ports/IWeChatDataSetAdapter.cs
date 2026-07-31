using WeChatVoice.Core.Models;

namespace WeChatVoice.Core.Ports;

/// <summary>
/// Version-specific adapter for a complete set of WeChat databases. Adapters
/// must refuse unknown schemas instead of guessing table or column meanings.
/// </summary>
public interface IWeChatDataSetAdapter
{
    string Id { get; }

    AdapterMatch Probe(WeChatDataSet dataSet);

    ValueTask<IVoiceCatalog> OpenAsync(
        WeChatDataSet dataSet,
        CancellationToken cancellationToken);
}

public interface IVoiceCatalog
{
    VoiceCatalogContext Context { get; }

    IAsyncEnumerable<ContactRecord> QueryContactsAsync(
        ContactQuery query,
        CancellationToken cancellationToken);

    IAsyncEnumerable<VoiceRecord> QueryVoicesAsync(
        VoiceQuery query,
        CancellationToken cancellationToken);

    ValueTask<Stream> OpenPayloadAsync(
        VoicePayloadLocator locator,
        CancellationToken cancellationToken);
}

public interface IWeChatDataSetAdapterResolver
{
    IWeChatDataSetAdapter Resolve(WeChatDataSet dataSet);
}

public sealed class NoMatchingDataSetAdapterException : InvalidOperationException
{
    public NoMatchingDataSetAdapterException(string dataSetId)
        : base($"No verified WeChat data-set adapter matches '{dataSetId}'. Probe the source schema and register an adapter before exporting.")
    {
        DataSetId = dataSetId;
    }

    public string DataSetId { get; }
}
