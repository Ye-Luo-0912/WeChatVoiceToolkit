using WeChatVoice.Core.Models;

namespace WeChatVoice.Core.Ports;

/// <summary>
/// A registered, versioned materialization backend. Formal backends have a
/// pinned executable identity; development-only adapters may opt into the
/// explicit untrusted path in the CLI.
/// </summary>
public interface IDatabaseMaterializationBackend
{
    string Id { get; }

    string Version { get; }

    string ExpectedBinarySha256 { get; }

    Task<VerifiedMaterialization> MaterializeAsync(
        VerifiedRawSnapshot snapshot,
        MaterializationOptions options,
        CancellationToken cancellationToken);
}
