namespace WeChatVoice.Windows;

/// <summary>
/// A minimal, non-sensitive description of a running WeChat process.
/// </summary>
public sealed record WeChatProcessInfo(
    int ProcessId,
    string ProcessName,
    string? ProductVersion = null);
