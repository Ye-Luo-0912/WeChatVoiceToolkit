using System.Collections.ObjectModel;

namespace WeChatVoice.Core.Models;

/// <summary>
/// User-controlled filters for dataset curation.  Successful export is never
/// treated as implicit training selection; the profile's selected IDs are the
/// only source of training selection.
/// </summary>
public sealed record DatasetCurationFilters
{
    public DatasetCurationFilters(
        long? MinimumDurationMs = null,
        long? MaximumDurationMs = null,
        long? MinimumByteLength = null,
        long? MaximumByteLength = null,
        bool IncludeUnknownDuration = false,
        bool IncomingOnly = true,
        IReadOnlyList<string>? ExcludedQualityFlags = null)
    {
        ValidateRange(MinimumDurationMs, MaximumDurationMs, nameof(MinimumDurationMs), "duration");
        ValidateRange(MinimumByteLength, MaximumByteLength, nameof(MinimumByteLength), "byte length");
        this.MinimumDurationMs = MinimumDurationMs;
        this.MaximumDurationMs = MaximumDurationMs;
        this.MinimumByteLength = MinimumByteLength;
        this.MaximumByteLength = MaximumByteLength;
        this.IncludeUnknownDuration = IncludeUnknownDuration;
        this.IncomingOnly = IncomingOnly;
        ExcludedQualityFlags = ExcludedQualityFlags ?? Array.Empty<string>();
        this.ExcludedQualityFlags = new ReadOnlyCollection<string>(ExcludedQualityFlags
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray());
    }

    public long? MinimumDurationMs { get; }
    public long? MaximumDurationMs { get; }
    public long? MinimumByteLength { get; }
    public long? MaximumByteLength { get; }
    public bool IncludeUnknownDuration { get; }
    public bool IncomingOnly { get; }
    public IReadOnlyList<string> ExcludedQualityFlags { get; }

    private static void ValidateRange(long? minimum, long? maximum, string parameterName, string label)
    {
        if (minimum is < 0 || maximum is < 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, $"{label} filters cannot be negative.");
        }

        if (minimum is not null && maximum is not null && minimum > maximum)
        {
            throw new ArgumentException($"The minimum {label} filter cannot exceed the maximum.", parameterName);
        }
    }
}

public sealed record DatasetSelectionProfile
{
    public const string CurrentProfileVersion = "dataset-selection-v1";

    public DatasetSelectionProfile(
        string ManifestSha256,
        string RunId,
        DatasetCurationFilters? Filters = null,
        IReadOnlyList<string>? SelectedItemIds = null,
        IReadOnlyList<string>? DuplicateRepresentativeItemIds = null,
        DateTimeOffset? UpdatedAtUtc = null,
        string ProfileVersion = CurrentProfileVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ManifestSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(RunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ProfileVersion);
        if (ManifestSha256.Length != 64 || !ManifestSha256.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("The selection profile requires a SHA-256 manifest binding.", nameof(ManifestSha256));
        }

        this.ManifestSha256 = ManifestSha256.ToLowerInvariant();
        this.RunId = RunId;
        this.Filters = Filters ?? new DatasetCurationFilters();
        this.SelectedItemIds = FreezeIds(SelectedItemIds);
        this.DuplicateRepresentativeItemIds = FreezeIds(DuplicateRepresentativeItemIds);
        this.UpdatedAtUtc = (UpdatedAtUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
        this.ProfileVersion = ProfileVersion;
    }

    public string ManifestSha256 { get; }
    public string RunId { get; }
    public DatasetCurationFilters Filters { get; }
    public IReadOnlyList<string> SelectedItemIds { get; }
    public IReadOnlyList<string> DuplicateRepresentativeItemIds { get; }
    public DateTimeOffset UpdatedAtUtc { get; }
    public string ProfileVersion { get; }

    private static IReadOnlyList<string> FreezeIds(IReadOnlyList<string>? values)
        => new ReadOnlyCollection<string>((values ?? Array.Empty<string>())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray());
}

public sealed record DatasetCurationItem(
    string ItemId,
    string RelativeAudioPath,
    string Sha256,
    long ByteLength,
    long? DurationMs,
    IReadOnlyList<string> QualityFlags,
    VoiceDirection Direction,
    string? DuplicateGroupId,
    int DuplicateGroupSize,
    bool PassesFilters,
    bool IsSelected,
    bool IsDuplicateRepresentative,
    TrainingEligibility TrainingEligibility);

public sealed record DatasetDuplicateGroup(
    string GroupId,
    string Sha256,
    IReadOnlyList<string> ItemIds,
    string? RepresentativeItemId);

public sealed record DatasetCurationRequest(
    string ExportDirectory,
    DatasetCurationFilters? Filters = null,
    IReadOnlyList<string>? SelectedItemIds = null,
    IReadOnlyList<string>? DuplicateRepresentativeItemIds = null,
    string? ManifestPath = null,
    string? ExpectedManifestSha256 = null);

public sealed record DatasetCurationResult(
    string ExportDirectory,
    string ManifestPath,
    string ManifestSha256,
    string RunId,
    IReadOnlyList<DatasetCurationItem> Items,
    IReadOnlyList<DatasetDuplicateGroup> DuplicateGroups,
    DatasetSelectionProfile Profile,
    long SelectedDurationMs,
    long SelectedByteLength);
