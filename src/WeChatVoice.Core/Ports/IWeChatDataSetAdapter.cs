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
        VerifiedLocalWorkspace workspace,
        CancellationToken cancellationToken);
}

public interface IVoiceCatalog : IAsyncDisposable
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

/// <summary>
/// Optional catalog capabilities used to keep metadata filters and global
/// limits in the verified adapter/query layer. A catalog must opt in only when
/// its adapter applies the byte-length predicate before the global limit.
/// </summary>
public interface IVoiceCatalogQueryCapabilities
{
    bool SupportsPayloadByteLengthFiltering { get; }
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
