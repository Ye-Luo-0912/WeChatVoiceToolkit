using WeChatVoice.Core.Models;

namespace WeChatVoice.Core.Ports;

/// <summary>
/// Converts a raw encrypted snapshot into an ordinary SQLite workspace. The
/// boundary deliberately carries no key material or arbitrary process command.
/// </summary>
public interface IDatabaseMaterializer
{
    string Id { get; }

    Task<VerifiedMaterialization> MaterializeAsync(
        VerifiedRawSnapshot snapshot,
        MaterializationOptions options,
        CancellationToken cancellationToken);
}
