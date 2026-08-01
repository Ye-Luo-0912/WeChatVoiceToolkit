using System.Globalization;
using WeChatVoice.Core.Models;

namespace WeChatVoice.Workflows.Workflows;

/// <summary>
/// Builds the exact voice query for a resolved 1:1 contact. The conversation
/// id is always the contact's stable ConversationId; a caller-supplied
/// conversation id must match it exactly.
/// </summary>
public static class VoiceQueryBuilder
{
    public static VoiceQuery Build(
        string? conversationId,
        ContactRecord contact,
        VoiceDirection? direction,
        DateTimeOffset? from,
        DateTimeOffset? to)
    {
        ArgumentNullException.ThrowIfNull(contact);
        if (!string.IsNullOrWhiteSpace(conversationId)
            && !string.Equals(conversationId, contact.ConversationId, StringComparison.Ordinal))
        {
            throw new ArgumentException("A conversation id conflicts with the selected contact's stable ConversationId.", nameof(conversationId));
        }

        return new VoiceQuery(
            conversationId ?? contact.ConversationId,
            direction,
            from,
            to,
            ContactUsername: contact.Username,
            ContactId: contact.ContactId);
    }

    public static DateTimeOffset? ParseUtc(string? value, string optionName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            throw new ArgumentException($"{optionName} is not a valid UTC date/time.");
        }

        return parsed;
    }
}
