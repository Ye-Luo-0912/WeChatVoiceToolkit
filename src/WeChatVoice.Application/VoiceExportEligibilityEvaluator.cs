using WeChatVoice.Core.Models;

namespace WeChatVoice.Application;

/// <summary>
/// The single export eligibility rule shared by Scan and Export.  A linked
/// BLOB is not sufficient on its own: the record must remain bound to the
/// verified catalog, the exact contact, and one expected speaker.
/// </summary>
public sealed class VoiceExportEligibilityEvaluator
{
    public VoiceExportEligibility Evaluate(
        VoiceRecord record,
        VoiceCatalogContext context,
        VoiceQuery query)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(query);
        var isGuidedSelection = !string.IsNullOrWhiteSpace(query.ContactUsername)
            && !string.IsNullOrWhiteSpace(query.ContactId);

        if (record.PayloadState != VoicePayloadState.Linked)
        {
            return VoiceExportEligibility.Rejected("payload-state", $"Payload state is {record.PayloadState}.");
        }

        if (record.PayloadByteLength is 0 or < 0
            || isGuidedSelection && record.PayloadByteLength is not > 0)
        {
            return VoiceExportEligibility.Rejected("payload-empty", "The linked payload has no positive byte length.");
        }

        if (record.PayloadLocator is null
            || string.IsNullOrWhiteSpace(record.PayloadLocator.LogicalRole)
            || string.IsNullOrWhiteSpace(record.PayloadLocator.BlobKey)
            || record.PayloadLocator.ShardNumber is < 0)
        {
            return VoiceExportEligibility.Rejected("payload-locator", "The linked payload lacks a valid locator.");
        }

        if (string.IsNullOrWhiteSpace(record.SourceStableKey))
        {
            return VoiceExportEligibility.Rejected("source-identity", "The voice record lacks a complete SourceStableKey.");
        }

        if (!HasCompleteCatalogIdentity(record, context, isGuidedSelection))
        {
            return VoiceExportEligibility.Rejected("provenance", "The voice record lacks complete catalog provenance.");
        }

        var expectedConversationId = query.ConversationId ?? record.ConversationId;
        var expectedContactId = query.ContactId ?? record.ConversationStableId;
        if (string.IsNullOrWhiteSpace(expectedConversationId)
            || string.IsNullOrWhiteSpace(expectedContactId)
            || !string.Equals(record.ConversationId, expectedConversationId, StringComparison.Ordinal)
            || !string.Equals(record.ConversationStableId, expectedContactId, StringComparison.Ordinal))
        {
            return VoiceExportEligibility.Rejected("contact-identity", "The voice record is not bound to the selected contact.");
        }

        var direction = query.Direction ?? record.Direction;
        if (record.Direction != direction)
        {
            return VoiceExportEligibility.Rejected("direction", "The voice record is outside the selected direction.");
        }

        var expectedSpeaker = direction == VoiceDirection.Incoming
            ? query.ContactUsername ?? record.SpeakerId
            : context.AccountId;
        if (isGuidedSelection
            && (string.IsNullOrWhiteSpace(expectedSpeaker)
                || !string.Equals(record.SpeakerId, expectedSpeaker, StringComparison.Ordinal)))
        {
            return VoiceExportEligibility.Rejected("speaker", "The voice record is not from the single expected speaker.");
        }

        return VoiceExportEligibility.Accepted;
    }

    private static bool HasCompleteCatalogIdentity(VoiceRecord record, VoiceCatalogContext context, bool requireCompleteProvenance)
        => !requireCompleteProvenance
            || (!string.IsNullOrWhiteSpace(record.DataSetId)
            && string.Equals(record.DataSetId, context.DatasetId, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(record.AdapterId)
            && string.Equals(record.AdapterId, context.AdapterId, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(record.AdapterFamily)
            && string.Equals(record.AdapterFamily, context.AdapterFamily, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(record.AdapterVersion)
            && string.Equals(record.AdapterVersion, context.AdapterVersion, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(record.AccountId)
            && string.Equals(record.AccountId, context.AccountId, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(record.AccountStableId)
            && string.Equals(record.AccountStableId, context.AccountId, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(record.SnapshotId)
            && string.Equals(record.SnapshotId, context.SnapshotId, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(record.SourceDatabase)
            && record.DatabaseFingerprints.Count > 0
            && record.DatabaseFingerprints.SequenceEqual(context.DatabaseFingerprints, StringComparer.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(record.MessagePrimaryKey)
            && !string.IsNullOrWhiteSpace(record.MediaPrimaryKey));
}

public sealed record VoiceExportEligibility(bool IsEligible, string? ReasonCode, string? Detail)
{
    public static VoiceExportEligibility Accepted { get; } = new(true, null, null);

    public static VoiceExportEligibility Rejected(string reasonCode, string detail)
        => new(false, reasonCode, detail);
}
