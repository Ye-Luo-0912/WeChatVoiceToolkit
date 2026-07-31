namespace WeChatVoice.Core.Models;

public sealed record SchemaInspectionOptions(
    bool IncludeLocalPaths = true,
    string? WalPath = null,
    string? ShmPath = null);
