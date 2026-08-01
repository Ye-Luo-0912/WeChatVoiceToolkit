namespace WeChatVoice.Core.Models;

/// <summary>
/// The association and content state of a voice payload. Only Linked is
/// eligible for export; the other states remain visible to scan/audit output.
/// </summary>
public enum VoicePayloadState
{
    Linked,
    Missing,
    Empty,
    InvalidHeader,
    Ambiguous,
}
