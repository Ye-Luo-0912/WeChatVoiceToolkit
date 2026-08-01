namespace WeChatVoice.Core.Errors;

/// <summary>
/// Typed transport/protocol failure codes of the one-shot Key Broker pipe.
/// These are deliberately separate from <see cref="ErrorCode"/>: domain codes
/// describe the user's data and environment, transport codes describe the
/// broker channel itself. Clients map the wire value to this enum and never
/// surface a raw string.
/// </summary>
public enum BrokerTransportErrorCode
{
    /// <summary>The request was missing, oversized, or violated the fixed field allowlist.</summary>
    MalformedRequest,

    /// <summary>The broker protocol version is not supported.</summary>
    UnsupportedProtocol,

    /// <summary>The requested operation is not supported by the one-shot Broker.</summary>
    UnsupportedOperation,

    /// <summary>The one-shot request line was missing or exceeded the bounded frame.</summary>
    RequestTooLarge,

    /// <summary>The requested snapshot manifest was not found.</summary>
    SnapshotNotFound,

    /// <summary>The operation was cancelled.</summary>
    Cancelled,

    /// <summary>The Broker hit an unexpected runtime failure and returned a bounded, non-sensitive response.</summary>
    BrokerInternal,

    /// <summary>The broker returned a code that maps to no known enum member.</summary>
    Unknown,
}
