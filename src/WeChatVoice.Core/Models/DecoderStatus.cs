namespace WeChatVoice.Core.Models;

/// <summary>
/// Product-level status of the configured SILK duration decoder. The host
/// surfaces this to the user so "duration is unknown" is never a silent
/// rabbit hole; it maps to a concrete, actionable state.
/// </summary>
public enum DecoderStatus
{
    /// <summary>No decoder is configured and none is reachable.</summary>
    Missing,

    /// <summary>A decoder is configured and its self-test succeeded.</summary>
    Available,

    /// <summary>A decoder is configured but is not a reviewed/trusted binary.</summary>
    UntrustedOrUnsupported,

    /// <summary>A decoder is configured but failed its self-test.</summary>
    FailedSelfTest,
}

/// <summary>
/// Read-only decoder status report. Non-sensitive: it carries only the status,
/// a short protocol/version label, and a non-sensitive reason. It never
/// includes key material, database data, or memory contents.
/// </summary>
public sealed record DecoderStatusReport(
    DecoderStatus Status,
    string? Protocol = null,
    string? Reason = null);
