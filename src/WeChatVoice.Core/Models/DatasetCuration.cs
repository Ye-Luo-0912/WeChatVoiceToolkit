using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

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
        DateTimeOffset UpdatedAtUtc = default,
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
        this.UpdatedAtUtc = (UpdatedAtUtc == default ? DateTimeOffset.UtcNow : UpdatedAtUtc).ToUniversalTime();
        this.ProfileVersion = ProfileVersion;
        SelectionFingerprint = DatasetSelectionFingerprint.Compute(
            this.ManifestSha256,
            this.ProfileVersion,
            this.Filters,
            this.SelectedItemIds,
            this.DuplicateRepresentativeItemIds);
    }

    public string ManifestSha256 { get; }
    public string RunId { get; }
    public DatasetCurationFilters Filters { get; }
    public IReadOnlyList<string> SelectedItemIds { get; }
    public IReadOnlyList<string> DuplicateRepresentativeItemIds { get; }
    public DateTimeOffset UpdatedAtUtc { get; }
    public string ProfileVersion { get; }
    public string SelectionFingerprint { get; }

    private static IReadOnlyList<string> FreezeIds(IReadOnlyList<string>? values)
        => new ReadOnlyCollection<string>((values ?? Array.Empty<string>())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray());
}

/// <summary>
/// Computes the stable identity of a curated training selection. The
/// fingerprint binds the immutable export manifest, curation policy, and
/// opaque item IDs; it deliberately excludes timestamps, paths, usernames,
/// and other local/provenance details.
/// </summary>
public static class DatasetSelectionFingerprint
{
    public const string CurrentVersion = "dataset-selection-fingerprint-v1";

    public static string Compute(
        string manifestSha256,
        string profileVersion,
        DatasetCurationFilters filters,
        IReadOnlyList<string> selectedItemIds,
        IReadOnlyList<string> duplicateRepresentativeItemIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileVersion);
        ArgumentNullException.ThrowIfNull(filters);
        ArgumentNullException.ThrowIfNull(selectedItemIds);
        ArgumentNullException.ThrowIfNull(duplicateRepresentativeItemIds);

        var canonical = new StringBuilder(512);
        AppendField(canonical, "version", CurrentVersion);
        AppendField(canonical, "manifest", manifestSha256.ToLowerInvariant());
        AppendField(canonical, "profile", profileVersion);
        AppendField(canonical, "minimum-duration", Format(filters.MinimumDurationMs));
        AppendField(canonical, "maximum-duration", Format(filters.MaximumDurationMs));
        AppendField(canonical, "minimum-bytes", Format(filters.MinimumByteLength));
        AppendField(canonical, "maximum-bytes", Format(filters.MaximumByteLength));
        AppendField(canonical, "include-unknown-duration", filters.IncludeUnknownDuration ? "1" : "0");
        AppendField(canonical, "incoming-only", filters.IncomingOnly ? "1" : "0");
        AppendList(canonical, "excluded-quality-flags", filters.ExcludedQualityFlags);
        AppendList(canonical, "selected-items", selectedItemIds);
        AppendList(canonical, "duplicate-representatives", duplicateRepresentativeItemIds);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();
    }

    private static void AppendList(StringBuilder builder, string name, IReadOnlyList<string> values)
    {
        AppendField(builder, name + ".count", values.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var value in values)
        {
            AppendField(builder, name + ".item", value);
        }
    }

    private static void AppendField(StringBuilder builder, string name, string? value)
    {
        value ??= string.Empty;
        builder.Append(name.Length.ToString(CultureInfo.InvariantCulture))
            .Append(':')
            .Append(name)
            .Append(value.Length.ToString(CultureInfo.InvariantCulture))
            .Append(':')
            .Append(value)
            .Append('\n');
    }

    private static string Format(long? value)
        => value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
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
    long SelectedByteLength)
{
    public string SelectionFingerprint => Profile.SelectionFingerprint;
}

public sealed record DatasetBuildRequest(
    string ExportDirectory,
    string? ProfilePath = null,
    string? ManifestPath = null,
    string? OutputDirectory = null);

public sealed record DatasetBuildRepairRequest(
    string ExportDirectory,
    string OutputDirectory,
    string? ProfilePath = null,
    string? ManifestPath = null);

public sealed record DatasetBuildVerificationIssue(
    string Code,
    string? RelativePath,
    string Detail);

public sealed record DatasetBuildVerificationResult(
    string OutputDirectory,
    bool IsValid,
    string? SelectionFingerprint,
    int ItemCount,
    long TotalDurationMs,
    long TotalByteLength,
    IReadOnlyList<DatasetBuildVerificationIssue> Issues);

public sealed record DatasetBuildResult(
    string OutputDirectory,
    string SelectionFingerprint,
    string ManifestPath,
    string DatasetManifestPath,
    string DatasetCsvPath,
    string BuildManifestPath,
    int ItemCount,
    long TotalDurationMs,
    long TotalByteLength,
    bool UsedHardLinks);

public sealed record DatasetBuildManifest
{
    public DatasetBuildManifest(
        string SelectionFingerprint,
        string SourceManifestSha256,
        string ProfileSha256,
        DateTimeOffset BuiltAtUtc,
        IReadOnlyList<DatasetBuildItem>? Items = null,
        string? DatasetManifestSha256 = null,
        string? DatasetCsvSha256 = null,
        string? ProfileOutputSha256 = null,
        string Format = "wechatvoice-dataset-build-v1")
    {
        this.SelectionFingerprint = SelectionFingerprint;
        this.SourceManifestSha256 = SourceManifestSha256;
        this.ProfileSha256 = ProfileSha256;
        this.BuiltAtUtc = BuiltAtUtc.ToUniversalTime();
        this.Items = (Items ?? Array.Empty<DatasetBuildItem>()).ToArray();
        this.DatasetManifestSha256 = DatasetManifestSha256;
        this.DatasetCsvSha256 = DatasetCsvSha256;
        this.ProfileOutputSha256 = ProfileOutputSha256;
        this.Format = Format;
    }

    public string SelectionFingerprint { get; }
    public string SourceManifestSha256 { get; }
    public string ProfileSha256 { get; }
    public DateTimeOffset BuiltAtUtc { get; }
    public IReadOnlyList<DatasetBuildItem> Items { get; }
    public string? DatasetManifestSha256 { get; }
    public string? DatasetCsvSha256 { get; }
    public string? ProfileOutputSha256 { get; }
    public string Format { get; }
}

public sealed record DatasetBuildItem(
    string ItemId,
    string RelativeAudioPath,
    string Sha256,
    long ByteLength,
    long? DurationMs);
