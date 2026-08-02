namespace WeChatVoice.Core.Models;

public sealed record MaterializationRecoveryAssessment(
    string OutputDirectory,
    string? State,
    bool CanRecover,
    bool WorkspaceDocumentPresent);
