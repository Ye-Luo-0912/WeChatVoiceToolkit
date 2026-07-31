using System.Collections.ObjectModel;

namespace WeChatVoice.Core.Models;

/// <summary>
/// The complete set of database artifacts needed to interpret one WeChat
/// account. A data set can contain multiple message/media shards.
/// </summary>
public sealed record WeChatDataSet
{
    public WeChatDataSet(string DataSetId, string? AccountId, IEnumerable<DatabaseArtifact> Databases)
    {
        if (string.IsNullOrWhiteSpace(DataSetId))
        {
            throw new ArgumentException("A data-set identifier is required.", nameof(DataSetId));
        }

        ArgumentNullException.ThrowIfNull(Databases);
        var databaseList = Databases.ToArray();
        if (databaseList.Length == 0)
        {
            throw new ArgumentException("At least one database artifact is required.", nameof(Databases));
        }

        this.DataSetId = DataSetId;
        this.AccountId = string.IsNullOrWhiteSpace(AccountId) ? null : AccountId;
        this.Databases = new ReadOnlyCollection<DatabaseArtifact>(databaseList);
    }

    public string DataSetId { get; }

    public string? AccountId { get; }

    public IReadOnlyList<DatabaseArtifact> Databases { get; }
}

public sealed record DatabaseArtifact
{
    public DatabaseArtifact(
        string LogicalRole,
        int? ShardNumber,
        string DatabasePath,
        string Sha256,
        SchemaSnapshot Schema)
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
        this.DatabasePath = Path.GetFullPath(DatabasePath);
        this.Sha256 = Sha256;
        this.Schema = Schema;
    }

    public string LogicalRole { get; }

    public int? ShardNumber { get; }

    public string DatabasePath { get; }

    public string Sha256 { get; }

    public SchemaSnapshot Schema { get; }
}

public sealed record AdapterMatch(bool IsMatch, int Score = 0, string? Reason = null)
{
    public static AdapterMatch NoMatch(string reason) => new(false, 0, reason);

    public static AdapterMatch Match(int score, string? reason = null) => new(true, score, reason);
}

public sealed record ContactQuery
{
    public ContactQuery(string? SearchTerm = null, int? MaximumResults = null)
    {
        if (MaximumResults is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumResults), "Maximum results must be greater than zero when specified.");
        }

        this.SearchTerm = string.IsNullOrWhiteSpace(SearchTerm) ? null : SearchTerm;
        this.MaximumResults = MaximumResults;
    }

    public string? SearchTerm { get; }

    public int? MaximumResults { get; }
}

public sealed record ContactRecord
{
    public ContactRecord(string ContactId, string? Username, string? DisplayName, string? Remark)
    {
        if (string.IsNullOrWhiteSpace(ContactId))
        {
            throw new ArgumentException("A contact identifier is required.", nameof(ContactId));
        }

        this.ContactId = ContactId;
        this.Username = Username;
        this.DisplayName = DisplayName;
        this.Remark = Remark;
    }

    public string ContactId { get; }

    public string? Username { get; }

    public string? DisplayName { get; }

    public string? Remark { get; }
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
        VoicePayloadLocator PayloadLocator)
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
    }

    public string MessageId { get; }

    public string ConversationId { get; }

    public DateTimeOffset OccurredAtUtc { get; }

    public VoiceDirection Direction { get; }

    public VoicePayloadLocator PayloadLocator { get; }
}
