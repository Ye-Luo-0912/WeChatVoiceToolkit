using WeChatVoice.Core.Models;
using WeChatVoice.Core.Ports;

namespace WeChatVoice.Infrastructure.Adapters;

/// <summary>
/// The single composition root for adapters used by CLI, probes, and future
/// desktop hosts. A non-matching adapter is still registered explicitly so the
/// registry cannot silently diverge between commands.
/// </summary>
public static class BuiltInAdapters
{
    public static IReadOnlyList<IWeChatDataSetAdapter> Create()
        => [new WeixinWindows4Adapter()];
}

/// <summary>
/// Reserved adapter identity for the current Weixin Windows 4.x family. It is
/// intentionally non-matching until a verified schema mapping is supplied;
/// it never guesses table or column meanings.
/// </summary>
public sealed class WeixinWindows4Adapter : IWeChatDataSetAdapter
{
    public string Id => "weixin-windows-4";

    public AdapterMatch Probe(WeChatDataSet dataSet)
        => AdapterMatch.NoMatch("The Weixin Windows 4.x schema mapping is not verified yet; provide schema evidence before enabling this adapter.");

    public ValueTask<IVoiceCatalog> OpenAsync(VerifiedLocalWorkspace workspace, CancellationToken cancellationToken)
        => throw new NoMatchingDataSetAdapterException(workspace.DataSet.DataSetId);
}
