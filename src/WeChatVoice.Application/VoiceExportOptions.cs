namespace WeChatVoice.Application;

/// <summary>
/// Controls an export run without coupling it to a particular CLI or UI.
/// </summary>
public sealed record VoiceExportOptions
{
    /// <summary>
    /// When true, each successfully copied SILK file is passed to the configured decoder.
    /// A decode failure does not discard the original SILK export.
    /// </summary>
    public bool DecodeToWav { get; init; }

    /// <summary>
    /// Maximum number of message payloads processed at once.
    /// </summary>
    public int MaxDegreeOfParallelism { get; init; } = 2;

    /// <summary>
    /// Optional immutable scan result to verify while the export query is
    /// streamed. Desktop guided exports set all three values; standalone CLI
    /// exports may leave them unset.
    /// </summary>
    public string? ExpectedResultSetFingerprint { get; init; }

    public int? ExpectedResultCount { get; init; }

    public long? ExpectedTotalPayloadBytes { get; init; }
}
