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
        string Sha256,
        SchemaSnapshot Schema,
        string? LocalPath = null,
        bool WalPresent = false,
        bool ShmPresent = false,
        string? CompletenessIssue = null)
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

        if (string.IsNullOrWhiteSpace(Sha256))
        {
            throw new ArgumentException("A database SHA-256 is required.", nameof(Sha256));
        }

        ArgumentNullException.ThrowIfNull(Schema);
        this.LogicalRole = LogicalRole;
        this.ShardNumber = ShardNumber;
        this.DatabasePath = DatabasePath;
        this.Sha256 = Sha256;
        this.Schema = Schema;
        this.LocalPath = LocalPath;
        this.WalPresent = WalPresent;
        this.ShmPresent = ShmPresent;
        this.CompletenessIssue = CompletenessIssue;
    }

    public string LogicalRole { get; }

    public int? ShardNumber { get; }

    public string DatabasePath { get; }

    public string? LocalPath { get; }

    public string Sha256 { get; }

    public SchemaSnapshot Schema { get; }

    public bool WalPresent { get; }

    public bool ShmPresent { get; }

    public string? CompletenessIssue { get; }
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
        VoicePayloadLocator PayloadLocator,
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
        string? SpeakerId = null)
    {
        if (string.IsNullOrWhiteSpace(MessageId))
        {
            throw new ArgumentException("A message identifier is required.", nameof(MessageId));
        }

        if (string.IsNullOrWhiteSpace(ConversationId))
        {
            throw new ArgumentException("A conversation identifier is required.", nameof(ConversationId));
        }

        ArgumentNullException.ThrowIfNull(PayloadLocator);
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
    }

    public string MessageId { get; }

    public string ConversationId { get; }

    public DateTimeOffset OccurredAtUtc { get; }

    public VoiceDirection Direction { get; }

    public VoicePayloadLocator PayloadLocator { get; }

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

    public string StableExportKey
        => string.Join("|", SnapshotId ?? "unknown-snapshot", AdapterId ?? "unknown-adapter", AccountId ?? "unknown-account", ShardId ?? "unknown-shard", ConversationId, SourceMessageKey);
}
