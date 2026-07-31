using System.Collections.ObjectModel;

namespace WeChatVoice.Core.Models;

/// <summary>
/// The complete set of database artifacts needed to interpret one WeChat
/// account. A data set can contain multiple message/media shards.
/// </summary>
public sealed record WeChatDataSet
{
    public WeChatDataSet(
        string DataSetId,
        string? AccountId,
        IReadOnlyList<DatabaseArtifact> Databases,
        string? SnapshotId = null,
        string? AdapterId = null)
    {
        if (string.IsNullOrWhiteSpace(DataSetId))
        {
            throw new ArgumentException("A data-set identifier is required.", nameof(DataSetId));
        }

        ArgumentNullException.ThrowIfNull(Databases);
        var databaseList = Databases.ToArray();
        this.DataSetId = DataSetId;
        this.AccountId = string.IsNullOrWhiteSpace(AccountId) ? null : AccountId;
        this.Databases = new ReadOnlyCollection<DatabaseArtifact>(databaseList);
        this.SnapshotId = SnapshotId;
        this.AdapterId = AdapterId;
    }

    public string DataSetId { get; }

    public string? AccountId { get; }

    public IReadOnlyList<DatabaseArtifact> Databases { get; }

    public string? SnapshotId { get; }

    public string? AdapterId { get; }
}

public sealed record DatabaseArtifact
{
    public DatabaseArtifact(
        string LogicalRole,
        int? ShardNumber,
        string DatabasePath,
        string MainSha256,
        SchemaSnapshot Schema,
        string? LocalPath = null,
        bool WalPresent = false,
        bool ShmPresent = false,
        string? CompletenessIssue = null,
        long MainLength = 0,
        string? WalSha256 = null,
        long? WalLength = null,
        string? ShmSha256 = null,
        long? ShmLength = null,
        string? DatabaseGroupFingerprint = null)
    {
        if (string.IsNullOrWhiteSpace(LogicalRole))
        {
            throw new ArgumentException("A logical database role is required.", nameof(LogicalRole));
        }

        if (ShardNumber is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ShardNumber), "A shard number cannot be negative.");
        }

        if (string.IsNullOrWhiteSpace(DatabasePath))
        {
            throw new ArgumentException("A database path is required.", nameof(DatabasePath));
        }

        if (string.IsNullOrWhiteSpace(MainSha256))
        {
            throw new ArgumentException("A database SHA-256 is required.", nameof(MainSha256));
        }

        ArgumentNullException.ThrowIfNull(Schema);
        this.LogicalRole = LogicalRole;
        this.ShardNumber = ShardNumber;
        this.DatabasePath = DatabasePath;
        this.MainSha256 = MainSha256;
        this.Schema = Schema;
        this.LocalPath = LocalPath;
        this.WalPresent = WalPresent;
        this.ShmPresent = ShmPresent;
        this.CompletenessIssue = CompletenessIssue;
        this.MainLength = MainLength;
        this.WalSha256 = WalSha256;
        this.WalLength = WalLength;
        this.ShmSha256 = ShmSha256;
        this.ShmLength = ShmLength;
        this.DatabaseGroupFingerprint = DatabaseGroupFingerprint;
    }

    public string LogicalRole { get; }

    public int? ShardNumber { get; }

    public string DatabasePath { get; }

    public string? LocalPath { get; }

    public string MainSha256 { get; }

    public SchemaSnapshot Schema { get; }

    public bool WalPresent { get; }

    public bool ShmPresent { get; }

    public string? CompletenessIssue { get; }

    public long MainLength { get; }

    public string? WalSha256 { get; }

    public long? WalLength { get; }

    public string? ShmSha256 { get; }

    public long? ShmLength { get; }

    public string? DatabaseGroupFingerprint { get; }
}

public sealed record AdapterMatch(bool IsMatch, int Score = 0, string? Reason = null)
{
    public static AdapterMatch NoMatch(string reason) => new(false, 0, reason);

    public static AdapterMatch Match(int score, string? reason = null) => new(true, score, reason);
}

public sealed record ContactQuery
{
    public ContactQuery(string? SearchTerm = null, int? MaximumResults = null, string? Username = null)
    {
        if (MaximumResults is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumResults), "Maximum results must be greater than zero when specified.");
        }

        this.SearchTerm = string.IsNullOrWhiteSpace(SearchTerm) ? null : SearchTerm;
        this.MaximumResults = MaximumResults;
        this.Username = string.IsNullOrWhiteSpace(Username) ? null : Username;
    }

    public string? SearchTerm { get; }

    public int? MaximumResults { get; }

    public string? Username { get; }
}

public sealed record ContactRecord
{
    public ContactRecord(
        string ContactId,
        string? Username,
        string? DisplayName,
        string? Remark,
        string? WeChatId = null,
        string? Nickname = null,
        string? ConversationId = null,
        string? SourceDatabase = null)
    {
        if (string.IsNullOrWhiteSpace(ContactId))
        {
            throw new ArgumentException("A contact identifier is required.", nameof(ContactId));
        }

        this.ContactId = ContactId;
        this.Username = Username;
        this.DisplayName = DisplayName;
        this.Remark = Remark;
        this.WeChatId = WeChatId;
        this.Nickname = Nickname;
        this.ConversationId = ConversationId;
        this.SourceDatabase = SourceDatabase;
    }

    public string ContactId { get; }

    public string? Username { get; }

    public string? DisplayName { get; }

    public string? Remark { get; }

    public string? WeChatId { get; }

    public string? Nickname { get; }

    public string? ConversationId { get; }

    public string? SourceDatabase { get; }
}

public sealed record VoicePayloadLocator
{
    public VoicePayloadLocator(string LogicalRole, int? ShardNumber, string BlobKey)
    {
        if (string.IsNullOrWhiteSpace(LogicalRole))
        {
            throw new ArgumentException("A logical payload role is required.", nameof(LogicalRole));
        }

        if (ShardNumber is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ShardNumber), "A shard number cannot be negative.");
        }

        if (string.IsNullOrWhiteSpace(BlobKey))
        {
            throw new ArgumentException("A payload locator key is required.", nameof(BlobKey));
        }

        this.LogicalRole = LogicalRole;
        this.ShardNumber = ShardNumber;
        this.BlobKey = BlobKey;
    }

    public string LogicalRole { get; }

    public int? ShardNumber { get; }

    public string BlobKey { get; }
}

public sealed record VoiceRecord
{
    public VoiceRecord(
        string MessageId,
        string ConversationId,
        DateTimeOffset OccurredAtUtc,
        VoiceDirection Direction,
        VoicePayloadLocator? PayloadLocator,
        string? SourceDatabase = null,
        int? ShardNumber = null,
        string? ShardId = null,
        string? SnapshotId = null,
        string? AdapterId = null,
        string? AccountId = null,
        string? SourceMessageKey = null,
        string? PayloadSha256 = null,
        long? PayloadByteLength = null,
        long? DurationMs = null,
        bool MediaLinked = true,
        string? SpeakerId = null,
        string? DataSetId = null,
        string? AdapterVersion = null,
        IReadOnlyList<string>? DatabaseFingerprints = null,
        string? AdapterFamily = null,
        string? AccountStableId = null,
        string? ConversationStableId = null,
        string? MessagePrimaryKey = null,
        string? MediaPrimaryKey = null,
        string? DecodedSha256 = null,
        long? DecodedByteLength = null)
    {
        if (string.IsNullOrWhiteSpace(MessageId))
        {
            throw new ArgumentException("A message identifier is required.", nameof(MessageId));
        }

        if (string.IsNullOrWhiteSpace(ConversationId))
        {
            throw new ArgumentException("A conversation identifier is required.", nameof(ConversationId));
        }

        if (MediaLinked && PayloadLocator is null)
        {
            throw new ArgumentException("A linked voice record must provide a payload locator.", nameof(PayloadLocator));
        }

        if (!MediaLinked && PayloadLocator is not null)
        {
            throw new ArgumentException("An unassociated voice record must not provide a payload locator.", nameof(PayloadLocator));
        }

        this.MessageId = MessageId;
        this.ConversationId = ConversationId;
        this.OccurredAtUtc = OccurredAtUtc.ToUniversalTime();
        this.Direction = Direction;
        this.PayloadLocator = PayloadLocator;
        this.SourceDatabase = SourceDatabase;
        this.ShardNumber = ShardNumber;
        this.ShardId = ShardId ?? ShardNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture);
        this.SnapshotId = SnapshotId;
        this.AdapterId = AdapterId;
        this.AccountId = AccountId;
        this.SourceMessageKey = SourceMessageKey ?? MessageId;
        this.PayloadSha256 = PayloadSha256;
        this.PayloadByteLength = PayloadByteLength;
        this.DurationMs = DurationMs;
        this.MediaLinked = MediaLinked;
        this.SpeakerId = SpeakerId;
        this.DataSetId = string.IsNullOrWhiteSpace(DataSetId) ? null : DataSetId;
        this.AdapterVersion = string.IsNullOrWhiteSpace(AdapterVersion) ? null : AdapterVersion;
        this.DatabaseFingerprints = new System.Collections.ObjectModel.ReadOnlyCollection<string>(
            (DatabaseFingerprints ?? Array.Empty<string>())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray());
        this.AdapterFamily = string.IsNullOrWhiteSpace(AdapterFamily) ? AdapterId : AdapterFamily;
        this.AccountStableId = string.IsNullOrWhiteSpace(AccountStableId) ? AccountId : AccountStableId;
        this.ConversationStableId = string.IsNullOrWhiteSpace(ConversationStableId) ? ConversationId : ConversationStableId;
        this.MessagePrimaryKey = string.IsNullOrWhiteSpace(MessagePrimaryKey) ? this.SourceMessageKey : MessagePrimaryKey;
        this.MediaPrimaryKey = string.IsNullOrWhiteSpace(MediaPrimaryKey)
            ? PayloadLocator is null
                ? null
                : string.Join(":", PayloadLocator.LogicalRole, PayloadLocator.ShardNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty, PayloadLocator.BlobKey)
            : MediaPrimaryKey;
        this.DecodedSha256 = string.IsNullOrWhiteSpace(DecodedSha256) ? null : DecodedSha256;
        this.DecodedByteLength = DecodedByteLength;
    }

    public string MessageId { get; }

    public string ConversationId { get; }

    public DateTimeOffset OccurredAtUtc { get; }

    public VoiceDirection Direction { get; }

    public VoicePayloadLocator? PayloadLocator { get; }

    public string? SourceDatabase { get; }

    public int? ShardNumber { get; }

    public string? ShardId { get; }

    public string? SnapshotId { get; }

    public string? AdapterId { get; }

    public string? AccountId { get; }

    public string SourceMessageKey { get; }

    public string? PayloadSha256 { get; }

    public long? PayloadByteLength { get; }

    public long? DurationMs { get; }

    public bool MediaLinked { get; }

    public string? SpeakerId { get; }

    public string? DataSetId { get; }

    public string? AdapterVersion { get; }

    public IReadOnlyList<string> DatabaseFingerprints { get; }

    public string? AdapterFamily { get; }

    public string? AccountStableId { get; }

    public string? ConversationStableId { get; }

    public string? MessagePrimaryKey { get; }

    public string? MediaPrimaryKey { get; }

    public string? DecodedSha256 { get; }

    public long? DecodedByteLength { get; }

    /// <summary>
    /// Source identity used for de-duplication. Snapshot and database
    /// provenance are intentionally excluded. A null result means the record
    /// is not safe to reuse across runs.
    /// </summary>
    public string? SourceStableKey
        => string.IsNullOrWhiteSpace(AdapterFamily)
            || string.IsNullOrWhiteSpace(AccountStableId)
            || string.IsNullOrWhiteSpace(ConversationStableId)
            || string.IsNullOrWhiteSpace(MessagePrimaryKey)
            || string.IsNullOrWhiteSpace(MediaPrimaryKey)
            ? null
            : string.Join("|", AdapterFamily, AccountStableId, ConversationStableId, MessagePrimaryKey, MediaPrimaryKey);

    public VoiceProvenance Provenance
        => new(SnapshotId, DataSetId, AdapterVersion, DatabaseFingerprints);
}
