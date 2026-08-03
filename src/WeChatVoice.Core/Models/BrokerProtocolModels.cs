namespace WeChatVoice.Core.Models;

/// <summary>
/// How a broker error is classified on the wire. <see cref="Domain"/> errors
/// carry an <see cref="WeChatVoice.Core.Errors.ErrorCode"/> name; transport
/// errors carry a <see cref="WeChatVoice.Core.Errors.BrokerTransportErrorCode"/>
/// name. Clients parse the code against the matching enum and never surface
/// raw strings.
/// </summary>
public enum BrokerErrorKind
{
    Domain,
    Transport,
}

/// <summary>
/// Terminal response of the one-shot Key Broker. Shared by the Broker host
/// (writer), the KeyBrokerClient (reader), and workflow hosts.
/// </summary>
public sealed record BrokerResponse(
    string Status,
    string? RequestId,
    string? ProfileId,
    string? MaterializationId,
    BrokerError? Error);

public sealed record BrokerError(
    BrokerErrorKind Kind,
    string Code,
    string Message,
    bool IsRetryable = false,
    string? SuggestedAction = null,
    string? NonSensitiveTechnicalContext = null);

/// <summary>
/// Non-terminal progress event emitted by the Broker during a materialization.
/// Carries only non-sensitive progress counters; never key or content data.
/// </summary>
public sealed record BrokerStageEvent(
    string Stage,
    long? ScannedBytes = null,
    int? Candidates = null,
    int? CompletedGroups = null,
    int? TotalGroups = null,
    int? CompletedDatabases = null,
    int? TotalDatabases = null,
    int? FirstUnvalidatedGroupOrdinal = null);

/// <summary>
/// Result of the no-data Broker self-test. It proves the elevated pipe and
/// Worker bundle path without reading Weixin memory or opening a database.
/// </summary>
public sealed record BrokerSelfTestResponse(
    string Status,
    string? RequestId,
    int BrokerProcessId,
    string WorkerBundleStatus,
    string? NonSensitiveReason = null,
    int? ClientProcessId = null,
    string? ClientSid = null);
