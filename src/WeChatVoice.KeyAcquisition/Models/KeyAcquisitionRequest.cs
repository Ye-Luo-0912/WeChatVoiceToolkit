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

public sealed record KeyAcquisitionOptions
{
    public KeyAcquisitionOptions(
        string profileId,
        TimeSpan maximumDuration,
        long maximumScanBytes = 64L * 1024 * 1024,
        int maximumCandidates = 256,
        bool allowExperimentalProfile = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        if (maximumDuration <= TimeSpan.Zero || maximumDuration > TimeSpan.FromMinutes(30))
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDuration), "The acquisition duration must be positive and no more than 30 minutes.");
        }

        if (maximumScanBytes is <= 0 or > 768L * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumScanBytes), "The acquisition scan budget must be positive and no more than 768 MiB.");
        }

        if (maximumCandidates is <= 0 or > 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCandidates), "The candidate budget must be between 1 and 4096.");
        }

        ProfileId = profileId;
        MaximumDuration = maximumDuration;
        MaximumScanBytes = maximumScanBytes;
        MaximumCandidates = maximumCandidates;
        AllowExperimentalProfile = allowExperimentalProfile;
    }

    public string ProfileId { get; }
    public TimeSpan MaximumDuration { get; }
    public long MaximumScanBytes { get; }
    public int MaximumCandidates { get; }
    public bool AllowExperimentalProfile { get; }

    public KeyAcquisitionBudget Budget => new(MaximumDuration, MaximumScanBytes, MaximumCandidates);
}

public sealed record KeyAcquisitionBudget
{
    public KeyAcquisitionBudget(TimeSpan maximumDuration, long maximumScanBytes, int maximumCandidates)
    {
        if (maximumDuration <= TimeSpan.Zero || maximumScanBytes <= 0 || maximumCandidates <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDuration), "Acquisition budgets must be positive.");
        }

        MaximumDuration = maximumDuration;
        MaximumScanBytes = maximumScanBytes;
        MaximumCandidates = maximumCandidates;
    }

    public TimeSpan MaximumDuration { get; }
    public long MaximumScanBytes { get; }
    public int MaximumCandidates { get; }
}

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
        try
        {
            ValidateBindings(this.Bindings);
        }
        catch
        {
            foreach (var binding in this.Bindings)
            {
                binding.ProtectedKeyMaterial.Dispose();
            }

            throw;
        }
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

    private void ValidateBindings(IReadOnlyList<DatabaseKeyBinding> bindings)
    {
        var groups = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in bindings)
        {
            ArgumentNullException.ThrowIfNull(binding);
            if (!string.Equals(binding.SnapshotId, SnapshotId, StringComparison.Ordinal)
                || !string.Equals(binding.KeyExtractionProfileId, ProfileId, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(binding.EncryptionProfileId)
                || string.IsNullOrWhiteSpace(binding.DatabaseGroupFingerprint)
                || !groups.Add(binding.DatabaseGroupFingerprint))
            {
                throw new InvalidDataException("Key bindings must be unique and bound to the acquisition SnapshotId, ProfileId, and database group.");
            }

            _ = binding.ProtectedKeyMaterial.Length;
        }
    }
}
