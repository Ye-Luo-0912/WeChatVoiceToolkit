namespace WeChatVoice.Core.Models;

/// <summary>
/// Immutable metadata for one voice message. Payload bytes deliberately do not
/// live on this model: callers obtain a fresh readable stream from
/// <c>IVoiceSource.OpenPayloadAsync</c> when they need them.
/// </summary>
public sealed record VoiceMessage
{
    public VoiceMessage(
        string MessageId,
        string ConversationId,
        DateTimeOffset OccurredAtUtc,
        VoiceDirection Direction,
        string? PayloadReference = null)
    {
        if (string.IsNullOrWhiteSpace(MessageId))
        {
            throw new ArgumentException("A message identifier is required.", nameof(MessageId));
        }

        if (string.IsNullOrWhiteSpace(ConversationId))
        {
            throw new ArgumentException("A conversation identifier is required.", nameof(ConversationId));
        }

        if (PayloadReference is { Length: 0 })
        {
            throw new ArgumentException("A payload reference cannot be empty.", nameof(PayloadReference));
        }

        this.MessageId = MessageId;
        this.ConversationId = ConversationId;
        this.OccurredAtUtc = OccurredAtUtc.ToUniversalTime();
        this.Direction = Direction;
        this.PayloadReference = PayloadReference;
    }

    public string MessageId { get; }

    public string ConversationId { get; }

    public DateTimeOffset OccurredAtUtc { get; }

    public VoiceDirection Direction { get; }

    /// <summary>
    /// An optional, source-private token used to locate the payload. It is not
    /// interpreted as a path by the Core or Application layers.
    /// </summary>
    public string? PayloadReference { get; }
}
