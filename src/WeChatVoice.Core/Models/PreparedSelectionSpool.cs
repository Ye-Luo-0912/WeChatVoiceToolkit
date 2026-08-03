namespace WeChatVoice.Core.Models;

/// <summary>
/// Private, temporary metadata storage for a prepared voice selection. The
/// spool contains no payload bytes; it only prevents a large scan result from
/// remaining in the managed heap until export completes.
/// </summary>
public sealed record PreparedSelectionSpoolDescriptor
{
    public PreparedSelectionSpoolDescriptor(
        string Path,
        int RecordCount,
        long ByteLength,
        string Sha256,
        string FormatVersion = CurrentFormatVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Path);
        if (!System.IO.Path.IsPathFullyQualified(Path))
        {
            throw new ArgumentException("A prepared-selection spool path must be absolute.", nameof(Path));
        }

        if (RecordCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(RecordCount));
        }

        if (ByteLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ByteLength));
        }

        if (string.IsNullOrWhiteSpace(Sha256)
            || Sha256.Length != 64
            || !Sha256.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("The prepared-selection spool must carry a SHA-256 hash.", nameof(Sha256));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(FormatVersion);
        this.Path = System.IO.Path.GetFullPath(Path);
        this.RecordCount = RecordCount;
        this.ByteLength = ByteLength;
        this.Sha256 = Sha256.ToLowerInvariant();
        this.FormatVersion = FormatVersion;
    }

    public const string CurrentFormatVersion = "prepared-selection-v1";

    public string Path { get; }

    public int RecordCount { get; }

    public long ByteLength { get; }

    public string Sha256 { get; }

    public string FormatVersion { get; }
}
