using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace WeChatVoice.Core.Models;

public enum SeedVcSourceType
{
    WeChat,
    Phone,
}

public enum SeedVcPrepareItemState
{
    Kept,
    Rejected,
}

public enum SeedVcTrainStatus
{
    NotStarted,
    Running,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>
/// Deterministic, local-only preparation policy for Seed-VC fine-tuning.
/// The policy intentionally stays independent from Python and CUDA versions.
/// </summary>
public sealed record SeedVcPrepareProfile
{
    public const string CurrentVersion = "seedvc-prepare-v1";

    public SeedVcPrepareProfile(
        double MinimumSeconds = 1,
        double MaximumSeconds = 30,
        double TargetChunkSeconds = 10,
        int AnchorWeight = 2,
        bool Denoise = false,
        bool NormalizeLoudness = false,
        string Version = CurrentVersion)
    {
        if (MinimumSeconds < 1 || MinimumSeconds > 30) throw new ArgumentOutOfRangeException(nameof(MinimumSeconds));
        if (MaximumSeconds < MinimumSeconds || MaximumSeconds > 30) throw new ArgumentOutOfRangeException(nameof(MaximumSeconds));
        if (TargetChunkSeconds < MinimumSeconds || TargetChunkSeconds > MaximumSeconds) throw new ArgumentOutOfRangeException(nameof(TargetChunkSeconds));
        if (AnchorWeight is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(AnchorWeight));
        ArgumentException.ThrowIfNullOrWhiteSpace(Version);
        this.MinimumSeconds = MinimumSeconds;
        this.MaximumSeconds = MaximumSeconds;
        this.TargetChunkSeconds = TargetChunkSeconds;
        this.AnchorWeight = AnchorWeight;
        this.Denoise = Denoise;
        this.NormalizeLoudness = NormalizeLoudness;
        this.Version = Version;
        Fingerprint = ComputeFingerprint(this);
    }

    public double MinimumSeconds { get; }
    public double MaximumSeconds { get; }
    public double TargetChunkSeconds { get; }
    public int AnchorWeight { get; }
    public bool Denoise { get; }
    public bool NormalizeLoudness { get; }
    public string Version { get; }
    public string Fingerprint { get; }

    public long MinimumDurationMs => checked((long)Math.Round(MinimumSeconds * 1000, MidpointRounding.AwayFromZero));
    public long MaximumDurationMs => checked((long)Math.Round(MaximumSeconds * 1000, MidpointRounding.AwayFromZero));
    public long TargetChunkDurationMs => checked((long)Math.Round(TargetChunkSeconds * 1000, MidpointRounding.AwayFromZero));

    public static string ComputeFingerprint(SeedVcPrepareProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var canonical = string.Join('\n',
            "seedvc-profile-fingerprint-v1",
            profile.Version,
            profile.MinimumSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            profile.MaximumSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            profile.TargetChunkSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            profile.AnchorWeight.ToString(CultureInfo.InvariantCulture),
            profile.Denoise ? "1" : "0",
            profile.NormalizeLoudness ? "1" : "0");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}

public sealed record SeedVcPrepareRequest(
    string DatasetDirectory,
    string? AnchorDirectory = null,
    string? OutputDirectory = null,
    SeedVcPrepareProfile? Profile = null);

/// <summary>One auditable source decision. Paths are deliberately omitted.</summary>
public sealed record SeedVcPrepareItem(
    SeedVcSourceType SourceType,
    string SourceHash,
    string? SourceItemId,
    int SegmentIndex,
    int CopyIndex,
    string? RelativeAudioPath,
    string? Sha256,
    long ByteLength,
    long? DurationMs,
    SeedVcPrepareItemState State,
    string? RejectedReason = null);

public sealed record SeedVcPrepareManifest
{
    public SeedVcPrepareManifest(
        string PrepFingerprint,
        string DatasetBuildFingerprint,
        SeedVcPrepareProfile Profile,
        DateTimeOffset GeneratedAtUtc,
        IReadOnlyList<SeedVcPrepareItem>? Items,
        int KeptCount,
        int RejectedCount,
        long TotalDurationMs,
        long TotalByteLength,
        string Format = "wechatvoice-seedvc-prep-v1")
    {
        this.PrepFingerprint = PrepFingerprint;
        this.DatasetBuildFingerprint = DatasetBuildFingerprint;
        this.Profile = Profile ?? throw new ArgumentNullException(nameof(Profile));
        this.GeneratedAtUtc = GeneratedAtUtc.ToUniversalTime();
        this.Items = new ReadOnlyCollection<SeedVcPrepareItem>((Items ?? Array.Empty<SeedVcPrepareItem>()).ToArray());
        this.KeptCount = KeptCount;
        this.RejectedCount = RejectedCount;
        this.TotalDurationMs = TotalDurationMs;
        this.TotalByteLength = TotalByteLength;
        this.Format = Format;
    }

    public string PrepFingerprint { get; }
    public string DatasetBuildFingerprint { get; }
    public SeedVcPrepareProfile Profile { get; }
    public DateTimeOffset GeneratedAtUtc { get; }
    public IReadOnlyList<SeedVcPrepareItem> Items { get; }
    public int KeptCount { get; }
    public int RejectedCount { get; }
    public long TotalDurationMs { get; }
    public long TotalByteLength { get; }
    public string Format { get; }
}

public sealed record SeedVcPrepareResult(
    string OutputDirectory,
    string ManifestPath,
    string SourcesJournalPath,
    string PrepFingerprint,
    string DatasetBuildFingerprint,
    int KeptCount,
    int RejectedCount,
    long TotalDurationMs,
    long TotalByteLength,
    bool Reused);

public sealed record SeedVcDoctorRequest(
    string? SeedVcRoot = null,
    string? PythonPath = null,
    string? ConfigPath = null);

public sealed record SeedVcDoctorReport(
    bool IsReady,
    string? SeedVcRoot,
    string? PythonCommand,
    string? PythonVersion,
    string? TorchVersion,
    bool? CudaAvailable,
    string? GpuName,
    string? ConfigPath,
    string? FfmpegPath,
    IReadOnlyList<string> Issues,
    DateTimeOffset CheckedAtUtc)
{
    public bool SeedVcCheckoutFound => !Issues.Contains("seedvc-root-missing", StringComparer.Ordinal);
}

public sealed record SeedVcTrainRequest(
    string PrepDirectory,
    string SeedVcRoot,
    string? PythonPath = null,
    string? ConfigPath = null,
    string? OutputDirectory = null,
    string? RunName = null,
    int BatchSize = 1,
    int MaxSteps = 1000,
    int MaxEpochs = 1000,
    int SaveEvery = 500,
    bool Resume = true);

public sealed record SeedVcCheckpoint(
    string RelativePath,
    long ByteLength,
    string Sha256);

public sealed record SeedVcTrainManifest(
    string RunId,
    string RunName,
    string PrepFingerprint,
    string ConfigSha256,
    string? ConfigRelativePath,
    string? PythonVersion,
    string? TorchVersion,
    string? GpuName,
    string PythonCommand,
    string TrainScriptRelativePath,
    IReadOnlyList<string> Arguments,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    SeedVcTrainStatus Status,
    int? ExitCode,
    string LogRelativePath,
    IReadOnlyList<SeedVcCheckpoint> Checkpoints,
    string Format = "wechatvoice-seedvc-train-v1");

public sealed record SeedVcTrainResult(
    string RunDirectory,
    string ManifestPath,
    string LogPath,
    string RunId,
    SeedVcTrainStatus Status,
    int? ExitCode,
    IReadOnlyList<SeedVcCheckpoint> Checkpoints);
