namespace WeChatVoice.Core.Models;

/// <summary>
/// A raw snapshot whose manifest file set, lengths, and hashes were checked
/// against the directory currently being consumed.
/// </summary>
public sealed record VerifiedRawSnapshot
{
    public VerifiedRawSnapshot(RawSnapshot Snapshot, DateTimeOffset VerifiedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(Snapshot);
        this.Snapshot = Snapshot;
        this.VerifiedAtUtc = VerifiedAtUtc.ToUniversalTime();
    }

    public RawSnapshot Snapshot { get; }

    public string SnapshotId => Snapshot.SnapshotId;

    public DateTimeOffset VerifiedAtUtc { get; }
}
