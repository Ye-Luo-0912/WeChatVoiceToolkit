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
        int? MaximumResults = null)
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
    }

    public string? ConversationId { get; }

    public VoiceDirection? Direction { get; }

    public DateTimeOffset? FromUtc { get; }

    public DateTimeOffset? ToUtc { get; }

    public int? MaximumResults { get; }
}
