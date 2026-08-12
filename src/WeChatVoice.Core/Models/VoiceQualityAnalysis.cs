using System.Collections.ObjectModel;

namespace WeChatVoice.Core.Models;

/// <summary>
/// Structured, non-sensitive audio quality metrics computed from a decoded PCM
/// WAV. These metrics are derived artifacts: they are never stored in the raw
/// SILK export and never become the source of truth for the payload. Consumers
/// (dataset curation, quality filtering) use the derived flags and numeric
/// metrics only for display and filtering.
/// </summary>
public sealed record VoiceQualityAnalysis
{
    public const string CurrentVersion = "voice-quality-analysis-v1";

    /// <summary>Fraction of sample groups whose amplitude is at or below the silence floor.</summary>
    public const double DefaultSilenceFloor = 0.001;

    /// <summary>Fraction of sample groups at maximum amplitude that flags clipping.</summary>
    public const double DefaultClippingThreshold = 0.001;

    public const string EmptyFlag = "quality-empty-audio";
    public const string SilenceFlag = "quality-silent";
    public const string ClippingFlag = "quality-clipping";
    public const string LowLevelFlag = "quality-low-level";
    public const string DecodeFailedFlag = "quality-decode-failed";

    public VoiceQualityAnalysis(
        bool DecodeSuccess,
        int SampleRate,
        int Channels,
        int BitsPerSample,
        long? DurationMs,
        double SilenceRatio,
        double ClippingRatio,
        double Rms,
        double Peak,
        IReadOnlyList<string>? Flags = null,
        string Version = CurrentVersion)
    {
        this.DecodeSuccess = DecodeSuccess;
        this.SampleRate = SampleRate;
        this.Channels = Channels;
        this.BitsPerSample = BitsPerSample;
        this.DurationMs = DurationMs;
        this.SilenceRatio = SilenceRatio;
        this.ClippingRatio = ClippingRatio;
        this.Rms = Rms;
        this.Peak = Peak;
        this.Flags = new ReadOnlyCollection<string>((Flags ?? Array.Empty<string>()).ToArray());
        this.Version = Version;
    }

    public bool DecodeSuccess { get; }
    public int SampleRate { get; }
    public int Channels { get; }
    public int BitsPerSample { get; }
    public long? DurationMs { get; }
    public double SilenceRatio { get; }
    public double ClippingRatio { get; }
    /// <summary>Root-mean-square of normalized samples in [0, 1].</summary>
    public double Rms { get; }
    /// <summary>Maximum absolute normalized sample in [0, 1].</summary>
    public double Peak { get; }
    public IReadOnlyList<string> Flags { get; }
    public string Version { get; }
}

/// <summary>
/// Computes <see cref="VoiceQualityAnalysis"/> from a decoded PCM WAV. The
/// analysis is streaming and bounded: it reads the WAV data chunk in one pass
/// without buffering the whole file, and it never modifies the WAV or the
/// source SILK.
/// </summary>
public static class VoiceQualityAnalyzer
{
    public static VoiceQualityAnalysis Analyze(
        Stream wavStream,
        long? expectedDurationMs = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(wavStream);
        if (!wavStream.CanRead)
        {
            throw new InvalidDataException("The WAV stream must be readable.");
        }

        // Locate the fmt and data chunks in one bounded pass over the header.
        int sampleRate = 0;
        int channels = 0;
        int bitsPerSample = 0;
        long dataStart = -1;
        long dataBytes = 0;

        var header = new byte[12];
        if (!ReadExactly(wavStream, header, cancellationToken)
            || !header.AsSpan(0, 4).SequenceEqual("RIFF"u8)
            || !header.AsSpan(8, 4).SequenceEqual("WAVE"u8))
        {
            return FailedAnalysis();
        }

        var chunkHeader = new byte[8];
        while (wavStream.Position + 8 <= wavStream.Length)
        {
            if (!ReadExactly(wavStream, chunkHeader, cancellationToken))
            {
                return FailedAnalysis();
            }

            var chunkSize = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader.AsSpan(4, 4));
            if (chunkSize > wavStream.Length - wavStream.Position)
            {
                return FailedAnalysis();
            }

            if (chunkHeader.AsSpan(0, 4).SequenceEqual("fmt "u8))
            {
                if (chunkSize < 16 || chunkSize > 4096)
                {
                    return FailedAnalysis();
                }

                var format = new byte[chunkSize];
                if (!ReadExactly(wavStream, format, cancellationToken))
                {
                    return FailedAnalysis();
                }

                var audioFormat = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(0, 2));
                channels = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(2, 2));
                sampleRate = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(format.AsSpan(4, 4));
                bitsPerSample = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(14, 2));
                if (audioFormat != 1 || channels <= 0 || sampleRate <= 0 || bitsPerSample is not (8 or 16 or 24 or 32))
                {
                    return FailedAnalysis();
                }
            }
            else if (chunkHeader.AsSpan(0, 4).SequenceEqual("data"u8))
            {
                dataStart = wavStream.Position;
                dataBytes = chunkSize;
                wavStream.Position += chunkSize;
            }
            else
            {
                wavStream.Position += chunkSize;
            }

            if ((chunkSize & 1) != 0 && wavStream.Position < wavStream.Length)
            {
                wavStream.Position++;
            }
        }

        if (dataStart < 0 || dataBytes == 0 || bitsPerSample == 0)
        {
            return FailedAnalysis();
        }

        return AnalyzeData(
            wavStream,
            dataStart,
            dataBytes,
            channels,
            bitsPerSample,
            sampleRate,
            expectedDurationMs,
            cancellationToken);
    }

    private static VoiceQualityAnalysis AnalyzeData(
        Stream wavStream,
        long dataStart,
        long dataBytes,
        int channels,
        int bitsPerSample,
        int sampleRate,
        long? expectedDurationMs,
        CancellationToken cancellationToken)
    {
        wavStream.Position = dataStart;
        var bytesPerSample = bitsPerSample / 8;
        var frameBytes = checked(bytesPerSample * channels);
        var framesAvailable = dataBytes / frameBytes;
        var silenceFloor = VoiceQualityAnalysis.DefaultSilenceFloor;
        var clippingThreshold = VoiceQualityAnalysis.DefaultClippingThreshold;

        const int BufferFrames = 4096;
        var buffer = new byte[checked(BufferFrames * frameBytes)];

        long groups = 0;
        long silentGroups = 0;
        long clippingGroups = 0;
        double sumSquares = 0;
        double peak = 0;
        long durationMs = 0;

        var remainingFrames = framesAvailable;
        var analyzer = CreateSampleAnalyzer(bitsPerSample);
        while (remainingFrames > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frames = (int)Math.Min(remainingFrames, BufferFrames);
            var bytesToRead = checked(frames * frameBytes);
            var read = 0;
            while (read < bytesToRead)
            {
                var n = wavStream.Read(buffer, read, bytesToRead - read);
                if (n <= 0)
                {
                    break;
                }

                read += n;
            }

            var frameCount = Math.Min(frames, read / frameBytes);
            for (var f = 0; f < frameCount; f++)
            {
                var frameOffset = f * frameBytes;
                double groupMax = 0;
                for (var c = 0; c < channels; c++)
                {
                    var sampleOffset = frameOffset + c * bytesPerSample;
                    var normalized = analyzer(buffer, sampleOffset);
                    var absolute = Math.Abs(normalized);
                    if (absolute > groupMax)
                    {
                        groupMax = absolute;
                    }

                    sumSquares += normalized * normalized;
                }

                // A "group" is one multi-channel frame; silence/clipping are
                // judged on the loudest channel of that instant.
                if (groupMax <= silenceFloor)
                {
                    silentGroups++;
                }

                if (groupMax >= 1.0)
                {
                    clippingGroups++;
                }

                if (groupMax > peak)
                {
                    peak = groupMax;
                }

                groups++;
            }

            remainingFrames -= frameCount;
            if (frameCount < frames)
            {
                break;
            }
        }

        if (groups == 0)
        {
            return FailedAnalysis();
        }

        var silenceRatio = (double)silentGroups / groups;
        var clippingRatio = (double)clippingGroups / groups;
        var rms = Math.Sqrt(sumSquares / (groups * channels));
        durationMs = sampleRate > 0 ? checked((long)((groups * 1000L) / sampleRate)) : 0;

        var flags = new List<string>(4);
        if (durationMs <= 0 || peak <= 0)
        {
            flags.Add(VoiceQualityAnalysis.EmptyFlag);
        }
        else
        {
            if (silenceRatio >= 0.95)
            {
                flags.Add(VoiceQualityAnalysis.SilenceFlag);
            }

            if (clippingRatio > clippingThreshold)
            {
                flags.Add(VoiceQualityAnalysis.ClippingFlag);
            }

            if (rms < 0.01)
            {
                flags.Add(VoiceQualityAnalysis.LowLevelFlag);
            }
        }

        // A decoded duration that disagrees sharply with the expected duration
        // (e.g. from the source metadata) is itself a quality signal.
        if (expectedDurationMs is { } expected && expected > 0 && durationMs > 0
            && (durationMs < expected / 2L || durationMs > expected * 2L))
        {
            flags.Add("quality-duration-mismatch");
        }

        return new VoiceQualityAnalysis(
            DecodeSuccess: true,
            sampleRate,
            channels,
            bitsPerSample,
            durationMs,
            silenceRatio,
            clippingRatio,
            rms,
            peak,
            flags);
    }

    private static Func<byte[], int, double> CreateSampleAnalyzer(int bitsPerSample)
        => bitsPerSample switch
        {
            8 => (buffer, offset) => (buffer[offset] - 128) / 128.0,
            16 => (buffer, offset) =>
                System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(offset, 2)) / 32768.0,
            24 => (buffer, offset) =>
            {
                var value = buffer[offset] | (buffer[offset + 1] << 8) | ((sbyte)buffer[offset + 2] << 16);
                return value / 8388608.0;
            }
            ,
            32 => (buffer, offset) =>
                System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offset, 4)) / 2147483648.0,
            _ => throw new InvalidDataException("Unsupported PCM bit depth."),
        };

    private static VoiceQualityAnalysis FailedAnalysis()
        => new(
            DecodeSuccess: false,
            SampleRate: 0,
            Channels: 0,
            BitsPerSample: 0,
            DurationMs: null,
            SilenceRatio: 0,
            ClippingRatio: 0,
            Rms: 0,
            Peak: 0,
            Flags: [VoiceQualityAnalysis.DecodeFailedFlag]);

    private static bool ReadExactly(Stream stream, Span<byte> buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        var local = new byte[buffer.Length];
        while (total < buffer.Length)
        {
            var read = stream.Read(local, total, buffer.Length - total);
            if (read <= 0)
            {
                break;
            }

            total += read;
        }

        if (total == buffer.Length)
        {
            local.AsSpan(0, buffer.Length).CopyTo(buffer);
        }

        return total == buffer.Length;
    }
}
