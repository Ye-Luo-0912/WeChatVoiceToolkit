using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace WeChatVoice.Core.Models;

/// <summary>
/// User-controlled settings for the derived WAV training build produced from a
/// curated SILK selection. The original SILK files remain the source of truth;
/// the WAV build is a rebuildable derived artifact. A decoder identity is
/// recorded when the build runs so a later decoder change invalidates only the
/// affected derived outputs.
/// </summary>
public sealed record AudioBuildProfile
{
    public const string CurrentVersion = "audio-build-v1";
    public const int DefaultSampleRate = 24000;

    public AudioBuildProfile(
        int SampleRate = DefaultSampleRate,
        bool Mono = true,
        AudioNormalizationPolicy Normalization = AudioNormalizationPolicy.None,
        string? DecoderIdentity = null,
        string Version = CurrentVersion)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(SampleRate);
        ArgumentException.ThrowIfNullOrWhiteSpace(Version);
        this.SampleRate = SampleRate;
        this.Mono = Mono;
        this.Normalization = Normalization;
        this.DecoderIdentity = DecoderIdentity;
        this.Version = Version;
        ProfileFingerprint = AudioBuildProfileFingerprint.Compute(this);
    }

    public int SampleRate { get; }
    public bool Mono { get; }
    public AudioNormalizationPolicy Normalization { get; }
    public string? DecoderIdentity { get; init; }
    public string Version { get; }

    /// <summary>
    /// Stable identity of the audio profile, binding the profile version and
    /// every user-controlled setting. The decoder identity is intentionally
    /// excluded from the profile fingerprint and recorded separately at build
    /// time so a profile change always creates a new build identity.
    /// </summary>
    public string ProfileFingerprint { get; }
}

public enum AudioNormalizationPolicy
{
    /// <summary>The decoded PCM WAV is written as-is with no level adjustment.</summary>
    None,
}

/// <summary>
/// Computes the stable identity of an <see cref="AudioBuildProfile"/>.
/// </summary>
public static class AudioBuildProfileFingerprint
{
    public const string CurrentVersion = "audio-build-profile-fingerprint-v1";

    public static string Compute(AudioBuildProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var canonical = new StringBuilder(128);
        AppendField(canonical, "version", AudioBuildProfile.CurrentVersion);
        AppendField(canonical, "profile", profile.Version);
        AppendField(canonical, "sample-rate", profile.SampleRate.ToString(CultureInfo.InvariantCulture));
        AppendField(canonical, "mono", profile.Mono ? "1" : "0");
        AppendField(canonical, "normalization", profile.Normalization.ToString());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();
    }

    private static void AppendField(StringBuilder builder, string name, string value)
    {
        builder.Append(name.Length.ToString(CultureInfo.InvariantCulture))
            .Append(':')
            .Append(name)
            .Append(value.Length.ToString(CultureInfo.InvariantCulture))
            .Append(':')
            .Append(value)
            .Append('\n');
    }
}

/// <summary>
/// Computes the stable identity of a derived dataset build. It binds the
/// curation selection fingerprint to the audio profile fingerprint so a change
/// to either produces a distinct, non-overwriting build output.
/// </summary>
public static class DatasetBuildFingerprint
{
    public const string CurrentVersion = "dataset-build-fingerprint-v1";

    public static string Compute(string selectionFingerprint, AudioBuildProfile? audioProfile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selectionFingerprint);
        var canonical = new StringBuilder(96);
        AppendField(canonical, "version", CurrentVersion);
        AppendField(canonical, "selection", selectionFingerprint.ToLowerInvariant());
        if (audioProfile is not null)
        {
            AppendField(canonical, "audio-profile", audioProfile.ProfileFingerprint);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();
    }

    private static void AppendField(StringBuilder builder, string name, string value)
    {
        builder.Append(name.Length.ToString(CultureInfo.InvariantCulture))
            .Append(':')
            .Append(name)
            .Append(value.Length.ToString(CultureInfo.InvariantCulture))
            .Append(':')
            .Append(value)
            .Append('\n');
    }
}