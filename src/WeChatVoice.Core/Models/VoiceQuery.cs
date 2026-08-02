namespace WeChatVoice.Core.Models;

/// <summary>
/// Optional filters used when enumerating voice messages.
/// </summary>
public sealed record VoiceQuery
{
    public VoiceQuery(
        string? ConversationId = null,
        VoiceDirection? Direction = null,
        DateTimeOffset? FromUtc = null,
        DateTimeOffset? ToUtc = null,
        int? MaximumResults = null,
        string? ContactUsername = null,
        string? ContactId = null,
        bool DeepScan = false,
        bool ResolveDuration = false)
    {
        if (MaximumResults is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumResults), "Maximum results must be greater than zero when specified.");
        }

        var normalizedFrom = FromUtc?.ToUniversalTime();
        var normalizedTo = ToUtc?.ToUniversalTime();
        if (normalizedFrom is not null && normalizedTo is not null && normalizedFrom > normalizedTo)
        {
            throw new ArgumentException("The start of the time range must not be after its end.", nameof(FromUtc));
        }

        this.ConversationId = string.IsNullOrWhiteSpace(ConversationId) ? null : ConversationId;
        this.Direction = Direction;
        this.FromUtc = normalizedFrom;
        this.ToUtc = normalizedTo;
        this.MaximumResults = MaximumResults;
        this.ContactUsername = string.IsNullOrWhiteSpace(ContactUsername) ? null : ContactUsername;
        this.ContactId = string.IsNullOrWhiteSpace(ContactId) ? null : ContactId;
        this.DeepScan = DeepScan;
        this.ResolveDuration = ResolveDuration;
    }

    public string? ConversationId { get; }

    public VoiceDirection? Direction { get; }

    public DateTimeOffset? FromUtc { get; }

    public DateTimeOffset? ToUtc { get; }

    public int? MaximumResults { get; }

    public string? ContactUsername { get; }

    /// <summary>
    /// Stable adapter-owned contact identity. Query consumers should prefer
    /// this over display names or a possibly changed username.
    /// </summary>
    public string? ContactId { get; }

    /// <summary>
    /// When true, the catalog may read and hash complete payload BLOBs. The
    /// default scan path reads only a bounded SILK header prefix.
    /// </summary>
    public bool DeepScan { get; init; }

    public bool ResolveDuration { get; }
}
