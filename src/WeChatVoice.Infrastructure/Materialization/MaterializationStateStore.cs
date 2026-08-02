using System.Text.Json;
using WeChatVoice.Core.Models;
using WeChatVoice.Infrastructure.Serialization;

namespace WeChatVoice.Infrastructure.Materialization;

/// <summary>
/// Persists the small commit marker beside a materialization manifest. The
/// marker is deliberately separate from the manifest so a database bundle can
/// be adopted after the workspace JSON write failed.
/// </summary>
public static class MaterializationStateStore
{
    public const string RelativeStatePath = ".wechatvoice/materialization-state.json";

    public static string GetPath(string outputRoot)
        => Path.Combine(Path.GetFullPath(outputRoot), RelativeStatePath.Replace('/', Path.DirectorySeparatorChar));

    public static bool IsStatePath(string? relativePath)
        => string.Equals(
            relativePath?.Replace('\\', '/'),
            RelativeStatePath,
            StringComparison.OrdinalIgnoreCase);

    public static Task WriteAsync(
        string outputRoot,
        string state,
        CancellationToken cancellationToken,
        string? failureCode = null)
    {
        if (!MaterializationCommitStates.IsKnown(state))
        {
            throw new ArgumentException($"Unknown materialization commit state '{state}'.", nameof(state));
        }

        var document = new MaterializationStateDocument(state, DateTimeOffset.UtcNow, failureCode);
        return AtomicFileWriter.WriteJsonAsync(
            GetPath(outputRoot),
            document,
            InfrastructureJson.Compact,
            cancellationToken);
    }

    public static async Task<MaterializationStateDocument> ReadAsync(
        string outputRoot,
        CancellationToken cancellationToken)
    {
        var path = GetPath(outputRoot);
        if (!File.Exists(path))
        {
            throw new InvalidDataException("The materialization commit state is missing.");
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var document = await JsonSerializer.DeserializeAsync<MaterializationStateDocument>(stream, InfrastructureJson.Compact, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The materialization commit state is empty.");
        if (!MaterializationCommitStates.IsKnown(document.State))
        {
            throw new InvalidDataException("The materialization commit state is unknown.");
        }

        return document;
    }
}
