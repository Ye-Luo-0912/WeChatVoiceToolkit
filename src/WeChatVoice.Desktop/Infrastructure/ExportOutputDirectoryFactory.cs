using System.Security.Cryptography;
using System.Text;

namespace WeChatVoice.Desktop.Infrastructure;

/// <summary>
/// Provides a stable, application-owned export destination. The path is based
/// on verified project/contact identities rather than a user profile path, so
/// returning to the Export page never requires the user to pick a folder.
/// </summary>
public sealed class ExportOutputDirectoryFactory
{
    private readonly string _applicationDataRoot;

    public ExportOutputDirectoryFactory(string applicationDataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDataRoot);
        _applicationDataRoot = Path.GetFullPath(applicationDataRoot);
    }

    public string CreateDefault(string workspaceId, string contactId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(contactId);

        var fingerprint = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"wechatvoice-export-v1|{workspaceId}|{contactId}")))
            .ToLowerInvariant()[..32];
        return Path.Combine(_applicationDataRoot, "Data", "Exports", fingerprint);
    }
}
