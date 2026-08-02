using System.Text.Json;
using System.Text.Json.Serialization;
using WeChatVoice.Core.Models;
using WeChatVoice.Infrastructure.Serialization;

namespace WeChatVoice.Infrastructure.Sqlite;

/// <summary>
/// The single writer for executable Workspace JSON documents. Keeping the
/// atomic write and serializer policy here prevents workflows and hosts from
/// creating subtly different trust-boundary documents.
/// </summary>
public static class LocalWorkspaceDocumentStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static Task WriteAsync(
        string path,
        LocalWorkspace workspace,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(workspace);
        return AtomicFileWriter.WriteJsonAsync(
            Path.GetFullPath(path),
            workspace,
            JsonOptions,
            cancellationToken);
    }
}
