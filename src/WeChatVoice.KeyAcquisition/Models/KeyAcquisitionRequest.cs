namespace WeChatVoice.KeyAcquisition.Models;

/// <summary>
/// The broker request is intentionally declarative. Callers cannot provide a
/// PID, address, length, module base, arbitrary process name, or decryptor
/// command.
/// </summary>
public sealed record KeyAcquisitionRequest(
    int ProtocolVersion,
    string RequestId,
    string SnapshotId,
    string Operation = "acquire-and-materialize");

public sealed record KeyAcquisitionOptions(
    string ProfileId,
    TimeSpan MaximumDuration,
    long MaximumScanBytes = 64L * 1024 * 1024,
    int MaximumCandidates = 256);

public sealed class VerifiedKeyAcquisition : IDisposable
{
    private int disposed;

    public VerifiedKeyAcquisition(
        string AcquisitionId,
        string SnapshotId,
        string ProfileId,
        IReadOnlyList<DatabaseKeyBinding> Bindings,
        DateTimeOffset AcquiredAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(AcquisitionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(SnapshotId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ProfileId);
        ArgumentNullException.ThrowIfNull(Bindings);
        this.AcquisitionId = AcquisitionId;
        this.SnapshotId = SnapshotId;
        this.ProfileId = ProfileId;
        this.Bindings = Bindings.ToArray();
        this.AcquiredAtUtc = AcquiredAtUtc.ToUniversalTime();
    }

    public string AcquisitionId { get; }

    public string SnapshotId { get; }

    public string ProfileId { get; }

    public IReadOnlyList<DatabaseKeyBinding> Bindings { get; }

    public DateTimeOffset AcquiredAtUtc { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        foreach (var binding in Bindings)
        {
            binding.ProtectedKeyMaterial.Dispose();
        }
    }
}
