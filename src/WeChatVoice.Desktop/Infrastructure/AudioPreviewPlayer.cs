using System.Runtime.InteropServices;

namespace WeChatVoice.Desktop.Infrastructure;

/// <summary>
/// Plays a transient WAV preview of a single curated SILK artifact. The desktop
/// decodes SILK to a temp WAV through the shared workflow boundary and uses the
/// Windows <c>PlaySound</c> API to play it; no audio data is persisted into the
/// raw export or the curated dataset.
/// </summary>
public interface IAudioPreviewPlayer : IDisposable
{
    /// <summary>Plays the WAV file at the given path. Any current playback is stopped.</summary>
    void Play(string wavPath);

    /// <summary>Stops current playback without releasing the shared audio session.</summary>
    void Stop();
}

/// <summary>
/// Windows-only preview player built on <c>winmm</c> <c>PlaySoundW</c>. It is
/// self-contained and needs no managed audio package. Playback is fire-and-forget
/// and asynchronous; callers own the lifetime of the WAV file.
/// </summary>
public sealed class WinmmAudioPreviewPlayer : IAudioPreviewPlayer
{
    private static readonly object Sync = new();
    private bool _disposed;

    public void Play(string wavPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(wavPath) || !File.Exists(wavPath))
        {
            return;
        }

        // SND_ASYNC | SND_FILENAME | SND_NODEFAULT. Playing asynchronously lets
        // a second Play() preempt the current one, matching stop-then-play.
        _ = PlaySoundW(wavPath, IntPtr.Zero, SndFilename | SndAsync | SndNodefault);
    }

    public void Stop()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        StopPlayback();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopPlayback();
    }

    private static void StopPlayback()
    {
        lock (Sync)
        {
            _ = PlaySoundW(null, IntPtr.Zero, 0);
        }
    }

    private const uint SndFilename = 0x00020000;
    private const uint SndAsync = 0x0001;
    private const uint SndNodefault = 0x0002;

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySoundW(string? pszSound, IntPtr hmod, uint fdwSound);
}
