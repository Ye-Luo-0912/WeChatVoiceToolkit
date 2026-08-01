namespace WeChatVoice.Core.Errors;

/// <summary>
/// Structured, non-sensitive error payload that travels across the CLI and
/// Broker boundary. <see cref="NonSensitiveTechnicalContext"/> is a stable
/// machine-readable qualifier (for example "weixin-process-not-found") and
/// <see cref="SuggestedAction"/> is a machine-readable action key that the
/// presentation layer can map to localized guidance. No member of this type
/// is safe to render verbatim to a user.
/// </summary>
public sealed record AppError(
    ErrorCode Code,
    bool IsRetryable,
    string SuggestedAction,
    string NonSensitiveTechnicalContext);
