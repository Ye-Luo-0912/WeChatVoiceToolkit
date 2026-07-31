using System.Collections.ObjectModel;

namespace WeChatVoice.Core.Models;

/// <summary>
/// Parameters for a consistent database-file snapshot.
/// </summary>
public sealed record SnapshotRequest
{
    public SnapshotRequest(string SourceDirectory, string OutputDirectory, bool RequireStableSource = true)
    {
        if (string.IsNullOrWhiteSpace(SourceDirectory))
        {
            throw new ArgumentException("A source directory is required.", nameof(SourceDirectory));
        }

        if (string.IsNullOrWhiteSpace(OutputDirectory))
        {
            throw new ArgumentException("An output directory is required.", nameof(OutputDirectory));
        }

        this.SourceDirectory = Path.GetFullPath(SourceDirectory);
        this.OutputDirectory = Path.GetFullPath(OutputDirectory);
        this.RequireStableSource = RequireStableSource;
    }

    public string SourceDirectory { get; }

    public string OutputDirectory { get; }

    public bool RequireStableSource { get; }
}

/// <summary>
/// Immutable description of the files included in a completed snapshot.
/// </summary>
public sealed record SnapshotManifest
{
    public SnapshotManifest(
        string SourceDirectory,
        string SnapshotDirectory,
        DateTimeOffset CreatedAtUtc,
        IEnumerable<SnapshotFileRecord>? Files = null)
    {
        if (string.IsNullOrWhiteSpace(SourceDirectory))
        {
            throw new ArgumentException("A source directory is required.", nameof(SourceDirectory));
        }

        if (string.IsNullOrWhiteSpace(SnapshotDirectory))
        {
            throw new ArgumentException("A snapshot directory is required.", nameof(SnapshotDirectory));
        }

        this.SourceDirectory = Path.GetFullPath(SourceDirectory);
        this.SnapshotDirectory = Path.GetFullPath(SnapshotDirectory);
        this.CreatedAtUtc = CreatedAtUtc.ToUniversalTime();
        this.Files = new ReadOnlyCollection<SnapshotFileRecord>((Files ?? Array.Empty<SnapshotFileRecord>()).ToArray());
    }

    public string SourceDirectory { get; }

    public string SnapshotDirectory { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public IReadOnlyList<SnapshotFileRecord> Files { get; }
}

/// <summary>
/// Integrity metadata for one copied snapshot file.
/// </summary>
public sealed record SnapshotFileRecord(
    string RelativePath,
    long ByteLength,
    string Sha256,
    DateTimeOffset SourceLastWriteTimeUtc);
