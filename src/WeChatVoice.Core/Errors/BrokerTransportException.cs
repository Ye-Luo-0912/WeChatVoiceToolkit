namespace WeChatVoice.Core.Errors;

/// <summary>
/// Thrown by broker clients when the one-shot Broker returns a transport-level
/// failure (protocol, cancellation, snapshot-not-found, internal). Domain
/// failures are thrown as <see cref="AppFailureException"/> with an
/// <see cref="ErrorCode"/>; hosts never need to parse raw wire strings.
/// </summary>
public sealed class BrokerTransportException : InvalidOperationException
{
    public BrokerTransportException(BrokerTransportErrorCode code, string message, string? requestId = null)
        : base(message)
    {
        Code = code;
        RequestId = requestId;
    }

    public BrokerTransportErrorCode Code { get; }

    public string? RequestId { get; }
}
