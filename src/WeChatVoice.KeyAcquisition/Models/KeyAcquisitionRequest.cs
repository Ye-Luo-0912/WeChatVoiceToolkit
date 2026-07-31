namespace WeChatVoice.KeyAcquisition.Models;

/// <summary>
/// The broker request is intentionally declarative. Callers cannot provide a
/// PID, address, length, module base, arbitrary process name, or decryptor
/// command.
/// </summary>
public sealed record KeyAcquisitionRequest(
    int ProtocolVersion,
    string RequestId,
    string Nonce,
    string SnapshotId,
    string SnapshotManifestPath,
    string Operation = "acquire-and-materialize");

public sealed record KeyAcquisitionOptions(
    string ProfileId,
    TimeSpan MaximumDuration,
    long MaximumScanBytes = 64L * 1024 * 1024,
    int MaximumCandidates = 256);

public sealed record VerifiedKeyAcquisition(
    string AcquisitionId,
    string SnapshotId,
    string ProfileId,
    IReadOnlyList<DatabaseKeyBinding> Bindings,
    DateTimeOffset AcquiredAtUtc);
