using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace WeChatVoice.Core.Models;

/// <summary>
/// The immutable output of a voice scan and the only selection accepted by a
/// guided export.  It deliberately contains both the query identity and the
/// identity of the records returned by the verified catalog.
/// </summary>
public sealed record PreparedVoiceSelection
{
    public const string CurrentSelectionEngineVersion = "voice-selection-v2";
    public const string NoDurationResolverVersion = "none";

    public PreparedVoiceSelection(
        string WorkspaceDocumentPath,
        string WorkspaceId,
        string DatasetId,
        string AccountId,
        string? SnapshotId,
        string AdapterId,
        string AdapterVersion,
        string ContactId,
        string ContactUsername,
        VoiceDirection Direction,
        DateTimeOffset? FromUtc,
        DateTimeOffset? ToUtc,
        int? MaximumResults,
        bool DeepScan,
        bool ResolveDurations,
        long? MinimumDurationMs,
        long? MaximumDurationMs,
        long? MinimumPayloadBytes,
        long? MaximumPayloadBytes,
        string QueryFingerprint,
        string ResultSetFingerprint,
        int ResultCount,
        long TotalPayloadBytes,
        string SelectionEngineVersion,
        string DurationResolverVersion,
        VoiceScanReport ScanReport,
        IReadOnlyList<VoiceRecord>? Records = null,
        PreparedSelectionSpoolDescriptor? Spool = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(WorkspaceDocumentPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(DatasetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(AccountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(AdapterId);
        ArgumentException.ThrowIfNullOrWhiteSpace(AdapterVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(ContactId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ContactUsername);
        ArgumentException.ThrowIfNullOrWhiteSpace(QueryFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(ResultSetFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(SelectionEngineVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(DurationResolverVersion);
        ArgumentNullException.ThrowIfNull(ScanReport);
        if (Records is not null && Records.Count > 0 && Spool is not null)
        {
            throw new ArgumentException("A prepared selection cannot use both in-memory records and a disk spool.", nameof(Spool));
        }

        if (Spool is not null && Spool.RecordCount < ResultCount)
        {
            throw new ArgumentException("The prepared-selection spool contains fewer records than the exportable scan result.", nameof(Spool));
        }

        if (!Path.IsPathFullyQualified(WorkspaceDocumentPath))
        {
            throw new ArgumentException("The prepared selection must retain an absolute Workspace document path.", nameof(WorkspaceDocumentPath));
        }

        if (ResultCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ResultCount));
        }

        if (TotalPayloadBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(TotalPayloadBytes));
        }

        if (ScanReport.ExportableVoiceCount != ResultCount
            || !string.Equals(ScanReport.ResultSetFingerprint, ResultSetFingerprint, StringComparison.OrdinalIgnoreCase)
            || ScanReport.TotalPayloadBytes != TotalPayloadBytes)
        {
            throw new ArgumentException("The prepared selection does not match its scan report.", nameof(ScanReport));
        }

        this.WorkspaceDocumentPath = Path.GetFullPath(WorkspaceDocumentPath);
        this.WorkspaceId = WorkspaceId;
        this.DatasetId = DatasetId;
        this.AccountId = AccountId;
        this.SnapshotId = string.IsNullOrWhiteSpace(SnapshotId) ? null : SnapshotId;
        this.AdapterId = AdapterId;
        this.AdapterVersion = AdapterVersion;
        this.ContactId = ContactId;
        this.ContactUsername = ContactUsername;
        this.Direction = Direction;
        this.FromUtc = FromUtc?.ToUniversalTime();
        this.ToUtc = ToUtc?.ToUniversalTime();
        this.MaximumResults = MaximumResults;
        this.DeepScan = DeepScan;
        this.ResolveDurations = ResolveDurations;
        this.MinimumDurationMs = MinimumDurationMs;
        this.MaximumDurationMs = MaximumDurationMs;
        this.MinimumPayloadBytes = MinimumPayloadBytes;
        this.MaximumPayloadBytes = MaximumPayloadBytes;
        this.QueryFingerprint = QueryFingerprint;
        this.ResultSetFingerprint = ResultSetFingerprint;
        this.ResultCount = ResultCount;
        this.TotalPayloadBytes = TotalPayloadBytes;
        this.SelectionEngineVersion = SelectionEngineVersion;
        this.DurationResolverVersion = DurationResolverVersion;
        this.ScanReport = ScanReport;
        this.Records = new System.Collections.ObjectModel.ReadOnlyCollection<VoiceRecord>(
            (Records ?? Array.Empty<VoiceRecord>()).ToArray());
        this.Spool = Spool;
    }

    public string WorkspaceDocumentPath { get; }
    public string WorkspaceId { get; }
    public string DatasetId { get; }
    public string AccountId { get; }
    public string? SnapshotId { get; }
    public string AdapterId { get; }
    public string AdapterVersion { get; }
    public string ContactId { get; }
    public string ContactUsername { get; }
    public VoiceDirection Direction { get; }
    public DateTimeOffset? FromUtc { get; }
    public DateTimeOffset? ToUtc { get; }
    public int? MaximumResults { get; }
    public bool DeepScan { get; }
    public bool ResolveDurations { get; }
    public long? MinimumDurationMs { get; }
    public long? MaximumDurationMs { get; }
    public long? MinimumPayloadBytes { get; }
    public long? MaximumPayloadBytes { get; }
    public string QueryFingerprint { get; }
    public string ResultSetFingerprint { get; }
    public int ResultCount { get; }
    public long TotalPayloadBytes { get; }
    public string SelectionEngineVersion { get; }
    public string DurationResolverVersion { get; }
    public VoiceScanReport ScanReport { get; }

    /// <summary>
    /// The exact metadata rows emitted by the verified catalog during the scan.
    /// A formal export consumes these records directly and opens only their
    /// payload locators; it must not re-query the catalog. The list is frozen
    /// so the query and result-set identity cannot drift in memory.
    /// </summary>
    public IReadOnlyList<VoiceRecord> Records { get; }

    public PreparedSelectionSpoolDescriptor? Spool { get; }

    public bool HasPreparedRecords => ResultCount == 0 || Records.Count > 0 || Spool is not null;

    public int PreparedRecordCount => Spool?.RecordCount ?? Records.Count;

    /// <summary>Compatibility name used by older Desktop presentation code.</summary>
    public string PlanFingerprint => QueryFingerprint;

    public static PreparedVoiceSelection Create(
        string workspaceDocumentPath,
        VerifiedLocalWorkspace workspace,
        VoiceCatalogContext catalogContext,
        ContactRecord contact,
        VoiceQuery query,
        VoiceScanReport report,
        string durationResolverVersion,
        IReadOnlyList<VoiceRecord>? records = null,
        PreparedSelectionSpoolDescriptor? spool = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(catalogContext);
        ArgumentNullException.ThrowIfNull(contact);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.ContactUsername);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.ContactId);
        if (query.Direction is not { } direction)
        {
            throw new ArgumentException("A prepared voice selection requires one explicit voice direction.", nameof(query));
        }

        var queryFingerprint = ComputeQueryFingerprint(
            workspace.Workspace.WorkspaceId,
            catalogContext,
            contact,
            query,
            CurrentSelectionEngineVersion,
            durationResolverVersion);
        return new PreparedVoiceSelection(
            workspaceDocumentPath,
            workspace.Workspace.WorkspaceId,
            catalogContext.DatasetId,
            catalogContext.AccountId ?? throw new InvalidDataException("The catalog lacks a stable account identity."),
            catalogContext.SnapshotId,
            catalogContext.AdapterId,
            catalogContext.AdapterVersion,
            contact.ContactId,
            contact.Username ?? throw new InvalidDataException("The selected contact lacks a stable username."),
            direction,
            query.FromUtc,
            query.ToUtc,
            query.MaximumResults,
            query.DeepScan,
            query.ResolveDuration,
            query.MinimumDurationMs,
            query.MaximumDurationMs,
            query.MinimumPayloadBytes,
            query.MaximumPayloadBytes,
            queryFingerprint,
            report.ResultSetFingerprint,
            report.ExportableVoiceCount,
            report.TotalPayloadBytes,
            CurrentSelectionEngineVersion,
            durationResolverVersion,
            report,
            records,
            spool);
    }

    public static string ComputeQueryFingerprint(
        string workspaceId,
        VoiceCatalogContext context,
        ContactRecord contact,
        VoiceQuery query,
        string selectionEngineVersion,
        string durationResolverVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(contact);
        ArgumentNullException.ThrowIfNull(query);
        var value = string.Join(
            "\n",
            workspaceId,
            context.DatasetId,
            context.AccountId ?? string.Empty,
            context.SnapshotId ?? string.Empty,
            context.AdapterId,
            context.AdapterVersion,
            contact.ContactId,
            contact.Username ?? string.Empty,
            contact.ConversationId ?? string.Empty,
            query.Direction?.ToString() ?? string.Empty,
            query.FromUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
            query.ToUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
            query.MaximumResults?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            query.DeepScan ? "1" : "0",
            query.ResolveDuration ? "1" : "0",
            query.MinimumDurationMs?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            query.MaximumDurationMs?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            query.MinimumPayloadBytes?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            query.MaximumPayloadBytes?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            selectionEngineVersion,
            durationResolverVersion);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }
}

public sealed record ExportDestination
{
    public ExportDestination(
        string OutputDirectory,
        ExportCompletionPolicy CompletionPolicy = ExportCompletionPolicy.ExactAllOrNothing)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(OutputDirectory);
        if (!Path.IsPathFullyQualified(OutputDirectory))
        {
            throw new ArgumentException("The export destination must be an absolute directory.", nameof(OutputDirectory));
        }

        this.OutputDirectory = Path.GetFullPath(OutputDirectory);
        this.CompletionPolicy = CompletionPolicy;
    }

    public string OutputDirectory { get; }
    public ExportCompletionPolicy CompletionPolicy { get; }
}
