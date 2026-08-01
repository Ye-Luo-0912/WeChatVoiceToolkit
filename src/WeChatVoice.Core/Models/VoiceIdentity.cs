using System.Collections.ObjectModel;

namespace WeChatVoice.Core.Models;

/// <summary>
/// Immutable context for every record emitted by one catalog. The context is
/// part of the catalog contract so an export cannot silently mix datasets or
/// adapter versions.
/// </summary>
public sealed record VoiceCatalogContext
{
    public VoiceCatalogContext(
        string DatasetId,
        string AdapterId,
        string AdapterVersion,
        string? AccountId,
        IReadOnlyList<string> DatabaseFingerprints,
        string? SnapshotId = null,
        string? AdapterFamily = null,
        MaterializationProvenance? MaterializationProvenance = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(DatasetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(AdapterId);
        ArgumentException.ThrowIfNullOrWhiteSpace(AdapterVersion);
        ArgumentNullException.ThrowIfNull(DatabaseFingerprints);
        var fingerprints = DatabaseFingerprints
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (fingerprints.Length == 0)
        {
            throw new ArgumentException("At least one database fingerprint is required.", nameof(DatabaseFingerprints));
        }

        this.DatasetId = DatasetId;
        this.AdapterId = AdapterId;
        this.AdapterVersion = AdapterVersion;
        this.AdapterFamily = string.IsNullOrWhiteSpace(AdapterFamily) ? AdapterId : AdapterFamily;
        this.AccountId = string.IsNullOrWhiteSpace(AccountId) ? null : AccountId;
        this.DatabaseFingerprints = new ReadOnlyCollection<string>(fingerprints);
        this.SnapshotId = string.IsNullOrWhiteSpace(SnapshotId) ? null : SnapshotId;
        this.MaterializationProvenance = MaterializationProvenance;
    }

    public string DatasetId { get; }

    public string? SnapshotId { get; }

    public string AdapterId { get; }

    public string AdapterVersion { get; }

    public string AdapterFamily { get; }

    public string? AccountId { get; }

    public IReadOnlyList<string> DatabaseFingerprints { get; }

    public MaterializationProvenance? MaterializationProvenance { get; }
}

/// <summary>
/// Provenance is audit metadata only. It intentionally never participates in
/// the source de-duplication key.
/// </summary>
public sealed record VoiceProvenance
{
    public VoiceProvenance(
        string? SnapshotId,
        string? DatasetId,
        string? AdapterVersion,
        IReadOnlyList<string>? DatabaseFingerprints)
    {
        this.SnapshotId = string.IsNullOrWhiteSpace(SnapshotId) ? null : SnapshotId;
        this.DatasetId = string.IsNullOrWhiteSpace(DatasetId) ? null : DatasetId;
        this.AdapterVersion = string.IsNullOrWhiteSpace(AdapterVersion) ? null : AdapterVersion;
        this.DatabaseFingerprints = new ReadOnlyCollection<string>(
            (DatabaseFingerprints ?? Array.Empty<string>())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray());
    }

    public string? SnapshotId { get; }

    public string? DatasetId { get; }

    public string? AdapterVersion { get; }

    public IReadOnlyList<string> DatabaseFingerprints { get; }
}
