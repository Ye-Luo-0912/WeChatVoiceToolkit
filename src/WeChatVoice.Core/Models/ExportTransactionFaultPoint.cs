namespace WeChatVoice.Core.Models;

/// <summary>
/// Deterministic checkpoints used by infrastructure tests to emulate a
/// process failure at a durable export boundary. Production composition does
/// not provide an injector.
/// </summary>
public enum ExportTransactionFaultPoint
{
    BeforeArtifactPublish,
    AfterArtifactPublish,
    BeforeItemJournalCommit,
    AfterItemJournalCommit,
    BeforeMetadataCommit,
    AfterMetadataCommit,
    AfterManifestCommit,
}
