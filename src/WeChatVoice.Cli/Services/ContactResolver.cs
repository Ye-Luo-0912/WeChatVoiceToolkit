using WeChatVoice.Core.Models;
using WeChatVoice.Core.Ports;

namespace WeChatVoice.Cli.Services;

public sealed class ContactResolver
{
    public async Task<ContactRecord> ResolveExactAsync(
        IVoiceCatalog catalog,
        string? username,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (string.IsNullOrWhiteSpace(username))
        {
            throw new ArgumentException("--contact-username is required for the audited voice path.");
        }

        var contacts = new List<ContactRecord>();
        await foreach (var contact in catalog.QueryContactsAsync(new ContactQuery(Username: username), cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            contacts.Add(contact);
        }

        if (contacts.Count != 1)
        {
            throw new InvalidOperationException($"Stable contact username '{username}' matched {contacts.Count} contacts; export requires exactly one match.");
        }

        var resolvedContact = contacts[0];
        if (string.IsNullOrWhiteSpace(resolvedContact.ContactId)
            || string.IsNullOrWhiteSpace(resolvedContact.ConversationId)
            || string.IsNullOrWhiteSpace(resolvedContact.Username))
        {
            throw new InvalidDataException("The selected contact lacks a stable ContactId, ConversationId, or Username.");
        }

        if (!string.Equals(resolvedContact.Username, username, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The adapter returned a contact whose stable username differs from the requested username.");
        }

        return resolvedContact;
    }
}
