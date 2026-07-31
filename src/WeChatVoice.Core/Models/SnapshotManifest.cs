using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;

namespace WeChatVoice.Core.Models;

/// <summary>
/// Parameters for a file-set snapshot. A live source is rejected by default;
/// callers must explicitly opt in and the resulting manifest is marked as
/// potentially inconsistent.
/// </summary>
public sealed record SnapshotRequest
{
    public SnapshotRequest(
        string SourceDirectory,
        string OutputDirectory,
        bool AllowLiveSource = false,
        int MaxAttempts = 3)
    {
        if (string.IsNullOrWhiteSpace(SourceDirectory))
        {
            throw new ArgumentException("A source directory is required.", nameof(SourceDirectory));
        }

        if (string.IsNullOrWhiteSpace(OutputDirectory))
        {
            throw new ArgumentException("An output directory is required.", nameof(OutputDirectory));
        }

        if (MaxAttempts is < 1 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxAttempts), "MaxAttempts must be between 1 and 10.");
        }

        this.SourceDirectory = Path.GetFullPath(SourceDirectory);
        this.OutputDirectory = Path.GetFullPath(OutputDirectory);
        this.AllowLiveSource = AllowLiveSource;
        this.MaxAttempts = MaxAttempts;
    }

    public string SourceDirectory { get; }

    public string OutputDirectory { get; }

    public bool AllowLiveSource { get; }

    public int MaxAttempts { get; }

    public bool RequireStableSource => !AllowLiveSource;
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
        IReadOnlyList<SnapshotFileRecord>? Files = null,
        bool PotentiallyInconsistent = false,
        int AttemptCount = 1,
        IReadOnlyList<string>? SourceProcessNames = null,
        string? SnapshotId = null,
        string? DisplayName = null)
    {
        if (string.IsNullOrWhiteSpace(SourceDirectory))
        {
            throw new ArgumentException("A source directory is required.", nameof(SourceDirectory));
        }

        if (AttemptCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(AttemptCount), "AttemptCount must be greater than zero.");
        }

        if (string.IsNullOrWhiteSpace(SnapshotDirectory))
        {
            throw new ArgumentException("A snapshot directory is required.", nameof(SnapshotDirectory));
        }

        this.SourceDirectory = Path.GetFullPath(SourceDirectory);
        this.SnapshotDirectory = Path.GetFullPath(SnapshotDirectory);
        this.CreatedAtUtc = CreatedAtUtc.ToUniversalTime();
        this.Files = new ReadOnlyCollection<SnapshotFileRecord>((Files ?? Array.Empty<SnapshotFileRecord>()).ToArray());
        this.PotentiallyInconsistent = PotentiallyInconsistent;
        this.AttemptCount = AttemptCount;
        this.SourceProcessNames = new ReadOnlyCollection<string>((SourceProcessNames ?? Array.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray());
        var computedSnapshotId = ComputeSnapshotId(this.Files);
        if (!string.IsNullOrWhiteSpace(SnapshotId) && !string.Equals(SnapshotId, computedSnapshotId, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("SnapshotId does not match the manifest file content.", nameof(SnapshotId));
        }

        this.SnapshotId = computedSnapshotId;
        this.DisplayName = string.IsNullOrWhiteSpace(DisplayName) ? null : DisplayName;
    }

    public string SourceDirectory { get; }

    public string SnapshotDirectory { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public IReadOnlyList<SnapshotFileRecord> Files { get; }

    public bool PotentiallyInconsistent { get; }

    public int AttemptCount { get; }

    public IReadOnlyList<string> SourceProcessNames { get; }

    public string SnapshotId { get; }

    public string? DisplayName { get; }

    public static string ComputeSnapshotId(IEnumerable<SnapshotFileRecord> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var canonical = string.Join("\n", files
            .OrderBy(static file => file.RelativePath.Replace('\\', '/'), StringComparer.Ordinal)
            .Select(static file => string.Join('|',
                file.RelativePath.Replace('\\', '/'),
                file.ByteLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
                file.Sha256.ToLowerInvariant())));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}

/// <summary>
/// Integrity metadata for one copied snapshot file.
/// </summary>
public sealed record SnapshotFileRecord(
    string RelativePath,
    long ByteLength,
    string Sha256,
    DateTimeOffset SourceLastWriteTimeUtc,
    string? FileId = null);
