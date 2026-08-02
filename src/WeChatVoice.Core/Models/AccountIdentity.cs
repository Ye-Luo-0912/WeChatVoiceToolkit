using System.Text.Json.Serialization;

namespace WeChatVoice.Core.Models;

/// <summary>
/// How strongly the workspace account identity is established.
/// <see cref="Candidate"/> means the path-derived candidate was verified to
/// exist in the account's own contact and Name2Id indexes but is not yet
/// proven to be "this account"; hosts must ask the user to confirm before
/// continuing. <see cref="Confirmed"/> is set only by a verified self-identity
/// field, never by a path or by convention.
/// </summary>
public enum AccountIdentityState
{
    Unknown,
    Candidate,
    Confirmed,
}

/// <summary>
/// The technical evidence level is independent from the user's decision to
/// process a path candidate. Path-derived evidence must never be promoted by
/// a confirmation click.
/// </summary>
public enum AccountEvidenceState
{
    Unknown,
    PathCandidate,
    DatabaseConfirmed,
}

public enum UserConfirmationState
{
    NotConfirmed,
    Confirmed,
}

public sealed record AccountIdentity(
    AccountIdentityState State,
    string? ConfirmedBy,
    UserConfirmationState UserConfirmation = UserConfirmationState.NotConfirmed,
    string? ConfirmedAccountId = null)
{
    public static AccountIdentity CandidateOnly { get; } = new(AccountIdentityState.Candidate, null);

    [JsonPropertyName("accountEvidenceState")]
    public AccountEvidenceState AccountEvidenceState
        => State switch
        {
            AccountIdentityState.Confirmed => AccountEvidenceState.DatabaseConfirmed,
            AccountIdentityState.Candidate => AccountEvidenceState.PathCandidate,
            _ => AccountEvidenceState.Unknown,
        };
}

/// <summary>
/// Non-sensitive account identity snapshot handed to the confirmation port and
/// future UI hosts. <see cref="AccountCandidate"/> is the display value
/// ("检测到账号：wxid_xxx"); it is not a secret.
/// </summary>
public sealed record AccountIdentityReport(
    string? AccountCandidate,
    AccountIdentityState State,
    string? ConfirmedBy);

public sealed record AccountConfirmation(bool Confirmed, string? ConfirmedAccountId);
