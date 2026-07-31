using System.Text.Json;
using System.Text.Json.Serialization;
using WeChatVoice.Core.Models;
using WeChatVoice.Infrastructure.Sqlite;

namespace WeChatVoice.Cli.Services;

public sealed class WorkspaceLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public async Task<LocalWorkspace> ReadAsync(string path, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var workspace = await JsonSerializer.DeserializeAsync<LocalWorkspace>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        return workspace ?? throw new InvalidDataException("The local workspace was empty.");
    }

    public async Task<VerifiedLocalWorkspace> LoadVerifiedAsync(string path, CancellationToken cancellationToken)
    {
        var workspace = await ReadAsync(path, cancellationToken).ConfigureAwait(false);
        return await new LocalWorkspaceVerifier().VerifyAsync(workspace, cancellationToken).ConfigureAwait(false);
    }
}
