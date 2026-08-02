using WeChatVoice.Core.Models;

namespace WeChatVoice.Infrastructure.Audio;

public static class VoiceDurationCachePath
{
    public const string RelativePath = ".wechatvoice/duration-cache.jsonl";

    public static string ForWorkspace(VerifiedLocalWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return Path.Combine(
            Path.GetFullPath(workspace.Workspace.SourceRoot),
            RelativePath.Replace('/', Path.DirectorySeparatorChar));
    }
}
