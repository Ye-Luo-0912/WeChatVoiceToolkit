using WeChatVoice.Core.Models;
using WeChatVoice.Core.Ports;

namespace WeChatVoice.Workflows.Workflows;

internal static class SelectionIdentity
{
    public static string DurationResolverVersion(IVoiceDurationResolver? resolver)
        => resolver switch
        {
            null => PreparedVoiceSelection.NoDurationResolverVersion,
            IVersionedVoiceDurationResolver versioned => versioned.DecoderVersion,
            _ => resolver.GetType().AssemblyQualifiedName
                ?? resolver.GetType().FullName
                ?? resolver.GetType().Name,
        };
}
