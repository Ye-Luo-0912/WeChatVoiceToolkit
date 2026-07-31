using WeChatVoice.Core.Ports;

namespace WeChatVoice.Infrastructure.Adapters;

/// <summary>
/// The single composition root for adapters used by CLI, probes, and future
/// desktop hosts. Each adapter remains responsible for exact schema matching;
/// the registry never selects one from filenames alone.
/// </summary>
public static class BuiltInAdapters
{
    public static IReadOnlyList<IWeChatDataSetAdapter> Create()
        => [new WeixinWindows4Adapter()];
}
