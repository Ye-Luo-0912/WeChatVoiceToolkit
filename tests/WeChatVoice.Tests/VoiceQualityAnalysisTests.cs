using System.Buffers.Binary;
using WeChatVoice.Core.Models;

namespace WeChatVoice.Tests;

public sealed class VoiceQualityAnalysisTests
{
    [Fact]
    public void Analyze_returns_duration_and_no_flags_for_healthy_16bit_pcm()
    {
        var samples = new short[24000];
        for (var i = 0; i < samples.Length; i++)
        {
            // A clear, moderate-amplitude tone that is neither silent, clipped,
            // nor at an unusually low level.
            samples[i] = (short)(Math.Sin(i / 20.0) * 8000);
        }

        using var wav = new MemoryStream(BuildWav(24000, 1, 16, samples));
        var analysis = VoiceQualityAnalyzer.Analyze(wav);

        Assert.True(analysis.DecodeSuccess);
        Assert.Equal(24000, analysis.SampleRate);
        Assert.Equal(1, analysis.Channels);
        Assert.Equal(16, analysis.BitsPerSample);
        Assert.True(analysis.DurationMs is > 0);
        Assert.True(analysis.Peak > 0);
        Assert.True(analysis.Rms > 0);
        Assert.DoesNotContain(VoiceQualityAnalysis.SilenceFlag, analysis.Flags);
        Assert.DoesNotContain(VoiceQualityAnalysis.ClippingFlag, analysis.Flags);
        Assert.DoesNotContain(VoiceQualityAnalysis.EmptyFlag, analysis.Flags);
    }

    [Fact]
    public void Analyze_flags_near_silent_audio_as_silent()
    {
        // Non-zero but below the silence floor, so the audio is not "empty"
        // yet every group is judged silent.
        var samples = new short[24000];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (short)((i & 1) == 0 ? 1 : -1);
        }

        using var wav = new MemoryStream(BuildWav(24000, 1, 16, samples));
        var analysis = VoiceQualityAnalyzer.Analyze(wav);

        Assert.True(analysis.DecodeSuccess);
        Assert.Contains(VoiceQualityAnalysis.SilenceFlag, analysis.Flags);
        Assert.Equal(1.0, analysis.SilenceRatio);
    }

    [Fact]
    public void Analyze_flags_constant_full_scale_as_clipping()
    {
        var samples = new short[24000];
        Array.Fill(samples, short.MinValue);
        using var wav = new MemoryStream(BuildWav(24000, 1, 16, samples));
        var analysis = VoiceQualityAnalyzer.Analyze(wav);

        Assert.True(analysis.DecodeSuccess);
        Assert.Contains(VoiceQualityAnalysis.ClippingFlag, analysis.Flags);
        Assert.True(analysis.ClippingRatio > 0);
    }

    [Fact]
    public void Analyze_flags_very_quiet_audio_as_low_level()
    {
        var samples = new short[24000];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (short)(Math.Sin(i / 20.0) * 10);
        }

        using var wav = new MemoryStream(BuildWav(24000, 1, 16, samples));
        var analysis = VoiceQualityAnalyzer.Analyze(wav);

        Assert.True(analysis.DecodeSuccess);
        Assert.Contains(VoiceQualityAnalysis.LowLevelFlag, analysis.Flags);
    }

    [Fact]
    public void Analyze_flags_duration_disagreement_when_expected_duration_is_far_off()
    {
        var samples = new short[24000];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (short)(Math.Sin(i / 20.0) * 8000);
        }

        using var wav = new MemoryStream(BuildWav(24000, 1, 16, samples));
        var analysis = VoiceQualityAnalyzer.Analyze(wav, expectedDurationMs: 10_000);

        Assert.True(analysis.DecodeSuccess);
        Assert.Contains("quality-duration-mismatch", analysis.Flags);
    }

    [Fact]
    public void Analyze_returns_decode_failed_for_non_wav_bytes()
    {
        using var wav = new MemoryStream("this is not a wav file at all"u8.ToArray());
        var analysis = VoiceQualityAnalyzer.Analyze(wav);

        Assert.False(analysis.DecodeSuccess);
        Assert.Contains(VoiceQualityAnalysis.DecodeFailedFlag, analysis.Flags);
    }

    [Fact]
    public void Analyze_returns_decode_failed_for_empty_data_chunk()
    {
        var samples = Array.Empty<short>();
        using var wav = new MemoryStream(BuildWav(24000, 1, 16, samples));
        var analysis = VoiceQualityAnalyzer.Analyze(wav);

        Assert.False(analysis.DecodeSuccess);
        Assert.Contains(VoiceQualityAnalysis.DecodeFailedFlag, analysis.Flags);
    }

    [Fact]
    public void Analyze_handles_stereo_16bit_with_multi_channel_group_metrics()
    {
        var left = new short[24000];
        var right = new short[24000];
        for (var i = 0; i < left.Length; i++)
        {
            left[i] = (short)(Math.Sin(i / 20.0) * 8000);
            right[i] = 0;
        }

        var interleaved = new short[left.Length * 2];
        for (var i = 0; i < left.Length; i++)
        {
            interleaved[i * 2] = left[i];
            interleaved[i * 2 + 1] = right[i];
        }

        using var wav = new MemoryStream(BuildWav(24000, 2, 16, interleaved));
        var analysis = VoiceQualityAnalyzer.Analyze(wav);

        Assert.True(analysis.DecodeSuccess);
        Assert.Equal(2, analysis.Channels);
        // One channel is silent but the other is not, so the group is not silent.
        Assert.DoesNotContain(VoiceQualityAnalysis.SilenceFlag, analysis.Flags);
    }

    private static byte[] BuildWav(int sampleRate, short channels, short bitsPerSample, short[] samples)
    {
        var bytesPerSample = bitsPerSample / 8;
        var dataBytes = checked(samples.Length * bytesPerSample);
        var wav = new byte[44 + dataBytes];
        "RIFF"u8.CopyTo(wav);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(4), (uint)(wav.Length - 8));
        "WAVE"u8.CopyTo(wav.AsSpan(8));
        "fmt "u8.CopyTo(wav.AsSpan(12));
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(22), (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(24), (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(28), (uint)(sampleRate * channels * bytesPerSample));
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(32), (ushort)(channels * bytesPerSample));
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(34), (ushort)bitsPerSample);
        "data"u8.CopyTo(wav.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(40), (uint)dataBytes);
        for (var i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(44 + i * bytesPerSample), samples[i]);
        }

        return wav;
    }
}
