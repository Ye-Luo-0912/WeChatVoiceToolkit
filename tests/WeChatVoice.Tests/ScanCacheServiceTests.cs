using WeChatVoice.Application;
using WeChatVoice.Core.Models;

namespace WeChatVoice.Tests;

public sealed class ScanCacheServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WeChatVoiceToolkit.ScanCacheTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private ScanCacheService CreateService() => new(_root);

    [Fact]
    public async Task Write_then_read_round_trips_report_and_records()
    {
        var service = CreateService();
        var workspaceId = "ws-1";
        var fingerprint = Fingerprint("a");
        var records = new[] { LinkedRecord("m1", 1), LinkedRecord("m2", 2) };
        var report = Report(records);

        await service.WriteAsync(workspaceId, fingerprint, report, records, spool: null);

        var cached = await service.TryReadAsync(workspaceId, fingerprint);
        Assert.NotNull(cached);
        Assert.Equal(report.ExportableVoiceCount, cached!.Report.ExportableVoiceCount);
        Assert.Equal(report.MatchedVoiceCount, cached.Report.MatchedVoiceCount);
        Assert.Equal(report.ResultSetFingerprint, cached.Report.ResultSetFingerprint);
        Assert.Equal(2, cached.Records.Count);
        Assert.Equal(records[0].MessageId, cached.Records[0].MessageId);
        Assert.Equal(records[0].PayloadSha256, cached.Records[0].PayloadSha256);
        Assert.Equal(records[1].DurationMs, cached.Records[1].DurationMs);
        Assert.Null(cached.Spool);
    }

    [Fact]
    public async Task Entries_are_isolated_by_workspace_and_fingerprint()
    {
        var service = CreateService();
        var records = new[] { LinkedRecord("m1", 1) };
        var report = Report(records);

        await service.WriteAsync("ws-1", Fingerprint("a"), report, records, spool: null);
        await service.WriteAsync("ws-2", Fingerprint("a"), report, records, spool: null);
        await service.WriteAsync("ws-1", Fingerprint("b"), report, records, spool: null);

        Assert.NotNull(await service.TryReadAsync("ws-1", Fingerprint("a")));
        Assert.NotNull(await service.TryReadAsync("ws-2", Fingerprint("a")));
        Assert.NotNull(await service.TryReadAsync("ws-1", Fingerprint("b")));
        Assert.Null(await service.TryReadAsync("ws-1", Fingerprint("c")));
        Assert.Null(await service.TryReadAsync("ws-3", Fingerprint("a")));
    }

    [Fact]
    public async Task Delete_removes_the_entry()
    {
        var service = CreateService();
        var workspaceId = "ws-1";
        var fingerprint = Fingerprint("a");
        var records = new[] { LinkedRecord("m1", 1) };
        var report = Report(records);

        await service.WriteAsync(workspaceId, fingerprint, report, records, spool: null);
        Assert.NotNull(await service.TryReadAsync(workspaceId, fingerprint));

        service.DeleteAsync(workspaceId, fingerprint);
        Assert.Null(await service.TryReadAsync(workspaceId, fingerprint));
    }

    [Fact]
    public async Task Corrupt_data_file_is_treated_as_a_miss_and_deleted()
    {
        var service = CreateService();
        var workspaceId = "ws-1";
        var fingerprint = Fingerprint("a");
        var records = new[] { LinkedRecord("m1", 1) };
        var report = Report(records);
        await service.WriteAsync(workspaceId, fingerprint, report, records, spool: null);

        var dataPath = Path.Combine(service.RootDirectory, workspaceId, fingerprint + ".jsonl");
        await File.AppendAllTextAsync(dataPath, "corrupt\n");

        Assert.Null(await service.TryReadAsync(workspaceId, fingerprint));
        Assert.False(File.Exists(dataPath), "A corrupt scan cache entry should be deleted.");
    }

    [Fact]
    public async Task Large_result_is_rehydrated_through_a_temporary_spool()
    {
        var service = CreateService();
        var workspaceId = "ws-1";
        var fingerprint = Fingerprint("large");
        var records = Enumerable.Range(1, PreparedSelectionSpool.InMemoryRecordLimit + 100)
            .Select(index => LinkedRecord($"m{index}", index))
            .ToArray();
        var report = Report(records);

        await service.WriteAsync(workspaceId, fingerprint, report, records, spool: null);
        var cached = await service.TryReadAsync(workspaceId, fingerprint);

        Assert.NotNull(cached);
        Assert.Equal(records.Length, cached!.Report.ExportableVoiceCount);
        Assert.NotNull(cached.Spool);
        Assert.Equal(records.Length, cached.Spool!.RecordCount);

        var rehydrated = new List<VoiceRecord>();
        await foreach (var record in PreparedSelectionSpool.ReadAsync(cached.Spool, CancellationToken.None))
        {
            rehydrated.Add(record);
        }

        Assert.Equal(records.Length, rehydrated.Count);
        Assert.Equal(records[^1].MessageId, rehydrated[^1].MessageId);
        Assert.Equal(records[^1].PayloadSha256, rehydrated[^1].PayloadSha256);
    }

    private static string Fingerprint(string suffix)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(suffix))).ToLowerInvariant();

    private static VoiceScanReport Report(IReadOnlyList<VoiceRecord> records)
    {
        var shards = new Dictionary<string, int> { ["0"] = records.Count };
        return new VoiceScanReport(
            records.Count,
            records.Sum(static record => record.DurationMs ?? 0),
            records.Min(static record => record.OccurredAtUtc),
            records.Max(static record => record.OccurredAtUtc),
            shards,
            UnassociatedMediaCount: 0,
            EmptyBlobCount: 0,
            SuspectedDuplicateCount: 0,
            InvalidHeaderCount: 0,
            AmbiguousPayloadCount: 0,
            PayloadStateCounts: new Dictionary<string, int> { ["Linked"] = records.Count },
            DeepScan: false,
            ExportableVoiceCount: records.Count,
            TotalPayloadBytes: records.Sum(static record => record.PayloadByteLength ?? 0),
            DurationKnownCount: records.Count,
            ResultSetFingerprint: "deadbeef");
    }

    private static VoiceRecord LinkedRecord(string id, double seconds)
        => new VoiceRecord(
            id,
            "conversation",
            DateTimeOffset.UtcNow.AddSeconds(-seconds),
            VoiceDirection.Incoming,
            new VoicePayloadLocator("media", 0, id),
            SourceDatabase: "media_0.db",
            ShardNumber: 0,
            SnapshotId: "snapshot",
            AdapterId: "adapter",
            AccountId: "account",
            PayloadSha256: System.Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(id))).ToLowerInvariant(),
            PayloadByteLength: 10,
            DurationMs: 1000,
            MediaLinked: true,
            SpeakerId: "conversation",
            DataSetId: "dataset",
            AdapterVersion: "v1",
            DatabaseFingerprints: ["fingerprint"],
            AdapterFamily: "adapter",
            AccountStableId: "account",
            ConversationStableId: "conversation",
            MessagePrimaryKey: id,
            MediaPrimaryKey: "media:" + id,
            PayloadState: VoicePayloadState.Linked);
}