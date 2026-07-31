using System.Security.Cryptography;

namespace WeChatVoice.Windows;

/// <summary>
/// Owns a small sensitive byte buffer and clears it deterministically when it is disposed.
/// The underlying array is never exposed directly.
/// </summary>
public sealed class SensitiveBuffer : IDisposable
{
    private byte[]? buffer;

    public SensitiveBuffer(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        buffer = new byte[length];
    }

    public SensitiveBuffer(ReadOnlySpan<byte> initialContents)
    {
        buffer = initialContents.ToArray();
    }

    /// <summary>
    /// Gets the fixed size of this buffer.
    /// </summary>
    public int Length => GetBuffer().Length;

    /// <summary>
    /// Replaces the buffer contents. The source must have exactly <see cref="Length"/> bytes.
    /// </summary>
    public void CopyFrom(ReadOnlySpan<byte> source)
    {
        var destination = GetBuffer();
        if (source.Length != destination.Length)
        {
            throw new ArgumentException("Source length must match the sensitive buffer length.", nameof(source));
        }

        source.CopyTo(destination);
    }

    /// <summary>
    /// Copies the current buffer contents into a caller-provided destination.
    /// </summary>
    public void CopyTo(Span<byte> destination)
    {
        var source = GetBuffer();
        if (destination.Length < source.Length)
        {
            throw new ArgumentException("Destination is too small for the sensitive buffer.", nameof(destination));
        }

        source.CopyTo(destination);
    }

    /// <summary>
    /// Immediately zeroes the current contents while retaining the buffer for reuse.
    /// </summary>
    public void Clear() => CryptographicOperations.ZeroMemory(GetBuffer());

    public void Dispose()
    {
        ClearAndRelease();
        GC.SuppressFinalize(this);
    }

    ~SensitiveBuffer() => ClearAndRelease();

    private byte[] GetBuffer() =>
        Volatile.Read(ref buffer) ?? throw new ObjectDisposedException(nameof(SensitiveBuffer));

    private void ClearAndRelease()
    {
        var releasedBuffer = Interlocked.Exchange(ref buffer, null);
        if (releasedBuffer is not null)
        {
            CryptographicOperations.ZeroMemory(releasedBuffer);
        }
    }
}
