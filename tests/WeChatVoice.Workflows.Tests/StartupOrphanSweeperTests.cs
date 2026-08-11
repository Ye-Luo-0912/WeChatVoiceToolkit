using WeChatVoice.Infrastructure.Storage;

namespace WeChatVoice.Workflows.Tests;

/// <summary>
/// Covers the host-startup orphan sweep: it removes only stale application-owned
/// staging/temp objects under known roots, refuses reparse points, and never
/// touches completed snapshot operations or workspace documents.
/// </summary>
public sealed class StartupOrphanSweeperTests
{
    [Fact]
    public void Sweep_removes_stale_nested_snapshot_staging()
    {
        using var temp = new SweepTemp();
        var staging = temp.Combine("Data", "Snapshots", "acct-1", ".op-1.snapshot-abc.staging");
        Directory.CreateDirectory(staging);
        File.WriteAllBytes(Path.Combine(staging, "payload.bin"), new byte[1]);
        Directory.SetLastWriteTimeUtc(staging, DateTime.UtcNow - TimeSpan.FromDays(2));

        var removed = new StartupOrphanSweeper(temp.Roots).Sweep(olderThan: TimeSpan.FromHours(1));

        Assert.Equal(1, removed);
        Assert.False(Directory.Exists(staging));
    }

    [Fact]
    public void Sweep_keeps_recent_staging_within_retention()
    {
        using var temp = new SweepTemp();
        var staging = temp.Combine("Data", "Snapshots", "acct-1", ".op-2.snapshot-def.staging");
        Directory.CreateDirectory(staging);
        File.WriteAllBytes(Path.Combine(staging, "payload.bin"), new byte[1]);
        Directory.SetLastWriteTimeUtc(staging, DateTime.UtcNow);

        var removed = new StartupOrphanSweeper(temp.Roots).Sweep(olderThan: TimeSpan.FromHours(1));

        Assert.Equal(0, removed);
        Assert.True(Directory.Exists(staging));
    }

    [Fact]
    public void Sweep_ignores_completed_snapshot_operations()
    {
        using var temp = new SweepTemp();
        var operation = temp.Combine("Data", "Snapshots", "acct-1", "op-3");
        Directory.CreateDirectory(Path.Combine(operation, ".wechatvoice"));
        File.WriteAllBytes(Path.Combine(operation, "payload.bin"), new byte[1]);
        Directory.SetLastWriteTimeUtc(operation, DateTime.UtcNow - TimeSpan.FromDays(2));

        var removed = new StartupOrphanSweeper(temp.Roots).Sweep(olderThan: TimeSpan.FromHours(1));

        Assert.Equal(0, removed);
        Assert.True(Directory.Exists(operation));
    }

    [Fact]
    public void Sweep_removes_stale_decoder_and_duration_temp()
    {
        using var temp = new SweepTemp();
        var decoderRoot = temp.Combine("DecoderTemp");
        var durationRoot = temp.Combine("DurationTemp");
        Directory.CreateDirectory(decoderRoot);
        Directory.CreateDirectory(durationRoot);
        var decoderInput = Path.Combine(decoderRoot, "abc.input.silk");
        var durationWav = Path.Combine(durationRoot, "abc.wav");
        File.WriteAllBytes(decoderInput, new byte[1]);
        File.WriteAllBytes(durationWav, new byte[1]);
        File.SetLastWriteTimeUtc(decoderInput, DateTime.UtcNow - TimeSpan.FromDays(2));
        File.SetLastWriteTimeUtc(durationWav, DateTime.UtcNow - TimeSpan.FromDays(2));

        var sweeper = new StartupOrphanSweeper(temp.Roots, decoderTempRoot: decoderRoot, durationTempRoot: durationRoot);
        var removed = sweeper.Sweep(olderThan: TimeSpan.FromHours(1));

        Assert.True(removed >= 2);
        Assert.False(File.Exists(decoderInput));
        Assert.False(File.Exists(durationWav));
    }

    [Fact]
    public void Sweep_skips_reparse_point_staging()
    {
        using var temp = new SweepTemp();
        var linkPath = temp.Combine("Data", "Snapshots", "acct-1", ".op-4.snapshot-xyz.staging");
        var target = temp.Combine("Data", "Snapshots", "acct-1", "target");
        Directory.CreateDirectory(target);
        try
        {
            Directory.CreateSymbolicLink(linkPath, target);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return; // Symlinks not permitted in this environment; skip.
        }

        Directory.SetLastWriteTimeUtc(linkPath, DateTime.UtcNow - TimeSpan.FromDays(2));
        var removed = new StartupOrphanSweeper(temp.Roots).Sweep(olderThan: TimeSpan.FromHours(1));

        Assert.Equal(0, removed);
        Assert.True(Directory.Exists(linkPath));
    }

    private sealed class SweepTemp : IDisposable
    {
        public SweepTemp()
        {
            Root = Path.Combine(Path.GetTempPath(), "WeChatVoiceToolkit.StartupSweepTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Roots = new StorageRoots(Combine(), Combine("Temp"));
        }

        public string Root { get; }
        public StorageRoots Roots { get; }

        public string Combine(params string[] segments) => Path.Combine([Root, .. segments]);

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}