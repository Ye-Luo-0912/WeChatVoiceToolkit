namespace WeChatVoice.Core.Errors;

/// <summary>
/// Exception that carries a stable <see cref="ErrorCode"/>. Hosts translate
/// the code through <see cref="ErrorCatalog"/> instead of parsing exception
/// messages. The message, when present, must stay non-sensitive.
/// </summary>
public sealed class AppFailureException : Exception
{
    public AppFailureException(ErrorCode code, string? message = null)
        : base(message)
    {
        Code = code;
    }

    public AppFailureException(ErrorCode code, string message, Exception innerException)
        : base(message, innerException)
    {
        ArgumentNullException.ThrowIfNull(innerException);
        Code = code;
    }

    public ErrorCode Code { get; }
}
