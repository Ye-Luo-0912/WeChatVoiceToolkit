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
        bool ResolveDuration = false,
        long? MinimumDurationMs = null,
        long? MaximumDurationMs = null,
        long? MinimumPayloadBytes = null,
        long? MaximumPayloadBytes = null)
    {
        if (MaximumResults is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumResults), "Maximum results must be greater than zero when specified.");
        }

        ValidateRange(MinimumDurationMs, MaximumDurationMs, nameof(MinimumDurationMs), "duration");
        ValidateRange(MinimumPayloadBytes, MaximumPayloadBytes, nameof(MinimumPayloadBytes), "payload size");

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
        this.MinimumDurationMs = MinimumDurationMs;
        this.MaximumDurationMs = MaximumDurationMs;
        this.MinimumPayloadBytes = MinimumPayloadBytes;
        this.MaximumPayloadBytes = MaximumPayloadBytes;
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

    public long? MinimumDurationMs { get; }

    public long? MaximumDurationMs { get; }

    public long? MinimumPayloadBytes { get; }

    public long? MaximumPayloadBytes { get; }

    public bool RequiresPostQueryFiltering
        => MinimumDurationMs is not null
            || MaximumDurationMs is not null
            || MinimumPayloadBytes is not null
            || MaximumPayloadBytes is not null;

    public VoiceQuery WithMaximumResults(int? maximumResults)
        => new(
            ConversationId,
            Direction,
            FromUtc,
            ToUtc,
            maximumResults,
            ContactUsername,
            ContactId,
            DeepScan,
            ResolveDuration,
            MinimumDurationMs,
            MaximumDurationMs,
            MinimumPayloadBytes,
            MaximumPayloadBytes);

    public VoiceQuery WithDeepScan(bool deepScan)
        => new(
            ConversationId,
            Direction,
            FromUtc,
            ToUtc,
            MaximumResults,
            ContactUsername,
            ContactId,
            deepScan,
            ResolveDuration,
            MinimumDurationMs,
            MaximumDurationMs,
            MinimumPayloadBytes,
            MaximumPayloadBytes);

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
