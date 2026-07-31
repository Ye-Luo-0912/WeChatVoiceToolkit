using WeChatVoice.Core.Models;

namespace WeChatVoice.Core.Ports;

public interface ILocalWorkspaceVerifier
{
    Task<VerifiedLocalWorkspace> VerifyAsync(
        LocalWorkspace workspace,
        CancellationToken cancellationToken);
}
