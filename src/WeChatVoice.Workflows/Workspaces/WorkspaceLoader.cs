using System.Text.Json;
using System.Text.Json.Serialization;
using WeChatVoice.Core.Models;
using WeChatVoice.Core.Ports;
using WeChatVoice.Infrastructure.Sqlite;

namespace WeChatVoice.Workflows.Workspaces;

/// <summary>
/// Reads and verifies local workspace JSON. The verifier is constructor-
/// injected so hosts (and tests) can substitute or extend it.
/// </summary>
public sealed class WorkspaceLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly ILocalWorkspaceVerifier _verifier;

    public WorkspaceLoader(ILocalWorkspaceVerifier? verifier = null)
        => _verifier = verifier ?? new LocalWorkspaceVerifier();

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
        return await _verifier.VerifyAsync(workspace, cancellationToken).ConfigureAwait(false);
    }
}
