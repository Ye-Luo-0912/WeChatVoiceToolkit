using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using WeChatVoice.Core.Models;
using WeChatVoice.Infrastructure.SeedVc;
using WeChatVoice.Infrastructure.Serialization;

namespace WeChatVoice.Tests;

public sealed class SeedVcPrepareServiceTests
{
    [Fact]
    public async Task Prepare_filters_short_audio_splits_long_audio_and_reuses_verified_output()
    {
        using var temp = new TestTemporaryDirectory();
        var dataset = temp.CreateDirectory("dataset");
        var audio = temp.CreateDirectory("dataset", "audio");
        var shortPath = WriteWave(audio, "short.wav", 4800); // 100 ms
        var longPath = WriteWave(audio, "long.wav", 3_360_000); // 35 s
        var shortHash = await FileHashing.ComputeSha256Async(shortPath, CancellationToken.None);
        var longHash = await FileHashing.ComputeSha256Async(longPath, CancellationToken.None);
        var manifest = new DatasetBuildManifest(
            "selection", "source", "profile", DateTimeOffset.UtcNow,
            [
                new DatasetBuildItem("short", "audio/short.wav", shortHash, new FileInfo(shortPath).Length, 100),
                new DatasetBuildItem("long", "audio/long.wav", longHash, new FileInfo(longPath).Length, 35_000),
            ],
            BuildFingerprint: "dataset-build");
        await WriteJsonAsync(Path.Combine(dataset, "build-manifest.json"), manifest);

        var output = temp.CreateDirectory("prep");
        Directory.Delete(output);
        var service = new SeedVcPrepareService();
        var request = new SeedVcPrepareRequest(dataset, OutputDirectory: output);
        var first = await service.PrepareAsync(request, CancellationToken.None);

        Assert.False(first.Reused);
        Assert.Equal(4, first.KeptCount);
        Assert.Equal(1, first.RejectedCount);
        Assert.Equal(4, Directory.EnumerateFiles(Path.Combine(output, "audio"), "*.wav").Count());
        await using (var manifestStream = File.OpenRead(first.ManifestPath))
        {
            Assert.Equal(1, (await JsonSerializer.DeserializeAsync<SeedVcPrepareManifest>(manifestStream, JsonOptions))!.RejectedCount);
        }

        var second = await service.PrepareAsync(request, CancellationToken.None);
        Assert.True(second.Reused);
        Assert.Equal(first.PrepFingerprint, second.PrepFingerprint);
    }

    [Fact]
    public async Task Prepare_applies_anchor_weight_and_changes_fingerprint()
    {
        using var temp = new TestTemporaryDirectory();
        var dataset = temp.CreateDirectory("dataset");
        var audio = temp.CreateDirectory("dataset", "audio");
        var datasetAudio = WriteWave(audio, "voice.wav", 96_000);
        var datasetHash = await FileHashing.ComputeSha256Async(datasetAudio, CancellationToken.None);
        await WriteJsonAsync(Path.Combine(dataset, "build-manifest.json"), new DatasetBuildManifest(
            "selection", "source", "profile", DateTimeOffset.UtcNow,
            [new DatasetBuildItem("voice", "audio/voice.wav", datasetHash, new FileInfo(datasetAudio).Length, 1_000)],
            BuildFingerprint: "dataset-build"));
        var anchors = temp.CreateDirectory("anchors");
        WriteWave(anchors, "anchor.wav", 96_000);

        var service = new SeedVcPrepareService();
        var one = await service.PrepareAsync(new SeedVcPrepareRequest(dataset, anchors, temp.GetPath("prep-one"), new SeedVcPrepareProfile(AnchorWeight: 1)), CancellationToken.None);
        var two = await service.PrepareAsync(new SeedVcPrepareRequest(dataset, anchors, temp.GetPath("prep-two"), new SeedVcPrepareProfile(AnchorWeight: 2)), CancellationToken.None);

        Assert.NotEqual(one.PrepFingerprint, two.PrepFingerprint);
        Assert.Equal(2, one.KeptCount);
        Assert.Equal(3, two.KeptCount);
    }

    private static async Task WriteJsonAsync<T>(string path, T value)
    {
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, value, JsonOptions);
    }

    private static string WriteWave(string directory, string name, int dataBytes)
    {
        var path = Path.Combine(directory, name);
        var bytes = new byte[44 + dataBytes];
        "RIFF"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), checked((uint)(36 + dataBytes)));
        "WAVE"u8.CopyTo(bytes.AsSpan(8));
        "fmt "u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), 48_000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), 96_000);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(32), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(34), 16);
        "data"u8.CopyTo(bytes.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), checked((uint)dataBytes));
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
