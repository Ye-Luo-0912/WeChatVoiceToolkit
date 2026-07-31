using WeChatVoice.Core.Models;

namespace WeChatVoice.Core.Ports;

public interface IRawSnapshotVerifier
{
    Task<VerifiedRawSnapshot> VerifyAsync(
        RawSnapshot snapshot,
        CancellationToken cancellationToken);
}
