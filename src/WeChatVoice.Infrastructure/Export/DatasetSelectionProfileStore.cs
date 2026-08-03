using System.Text.Json;
using WeChatVoice.Core.Models;
using WeChatVoice.Infrastructure.Serialization;

namespace WeChatVoice.Infrastructure.Export;

/// <summary>
/// Persists the user's curation choices next to an export.  The profile binds
/// to the exact manifest hash and contains only opaque item IDs, filters, and
/// selection state.
/// </summary>
public sealed class DatasetSelectionProfileStore
{
    public const string ProfileFileName = "selection-profile.json";

    public static string GetPath(string exportDirectory)
        => Path.Combine(Path.GetFullPath(exportDirectory), ProfileFileName);

    public async Task WriteAsync(
        string exportDirectory,
        DatasetSelectionProfile profile,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exportDirectory);
        ArgumentNullException.ThrowIfNull(profile);
        await using var exportLock = await ExportRootLock.AcquireAsync(
            Path.GetFullPath(exportDirectory),
            ExportRootLockMode.Exclusive,
            Guid.NewGuid().ToString("N"),
            runId: profile.RunId,
            cancellationToken).ConfigureAwait(false);
        var path = GetPath(exportDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await AtomicFileWriter.WriteJsonAsync(path, profile, InfrastructureJson.Indented, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DatasetSelectionProfile> ReadAsync(
        string exportDirectory,
        CancellationToken cancellationToken)
    {
        var path = GetPath(exportDirectory);
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            32 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<DatasetSelectionProfile>(
                stream,
                InfrastructureJson.Compact,
                cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The dataset selection profile is empty.");
    }
}
