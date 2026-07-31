using WeChatVoice.Core.Models;
using WeChatVoice.KeyAcquisition;
using WeChatVoice.KeyAcquisition.Models;
using WeChatVoice.KeyAcquisition.Ports;
using WeChatVoice.Windows;

namespace WeChatVoice.Tests;

public sealed class EphemeralAcquireAndMaterializeTests
{
    [Fact]
    public async Task ExecuteAsync_passes_group_bound_key_only_to_materializer_and_disposes_it_before_returning()
    {
        using var temporary = new TestTemporaryDirectory();
        var snapshot = CreateSnapshot(temporary);
        var key = new SensitiveBuffer(new byte[] { 1, 2, 3, 4 });
        var acquisition = CreateAcquisition(snapshot.SnapshotId, key);
        var acquisitionService = new FakeAcquisitionService(acquisition);
        var materializer = new FakeEphemeralMaterializer(temporary.GetPath("output"));
        var service = new EphemeralAcquireAndMaterializeService(acquisitionService, materializer);

        var result = await service.ExecuteAsync(
            snapshot,
            new KeyAcquisitionOptions("fake-profile", TimeSpan.FromSeconds(1)),
            new MaterializationOptions(temporary.GetPath("output")),
            CancellationToken.None);

        Assert.Equal(new byte[] { 1, 2, 3, 4 }, materializer.ObservedKey);
        Assert.Equal(snapshot.SnapshotId, result.Result.SourceSnapshotId);
        Assert.Throws<ObjectDisposedException>(() => key.CopyTo(new byte[4]));
    }

    [Fact]
    public async Task ExecuteAsync_disposes_key_when_materialization_fails()
    {
        using var temporary = new TestTemporaryDirectory();
        var snapshot = CreateSnapshot(temporary);
        var key = new SensitiveBuffer(new byte[] { 5, 6, 7, 8 });
        var acquisition = CreateAcquisition(snapshot.SnapshotId, key);
        var service = new EphemeralAcquireAndMaterializeService(
            new FakeAcquisitionService(acquisition),
            new ThrowingMaterializer());

        await Assert.ThrowsAsync<InvalidDataException>(() => service.ExecuteAsync(
            snapshot,
            new KeyAcquisitionOptions("fake-profile", TimeSpan.FromSeconds(1)),
            new MaterializationOptions(temporary.GetPath("output")),
            CancellationToken.None));

        Assert.Throws<ObjectDisposedException>(() => key.CopyTo(new byte[4]));
    }

    [Fact]
    public async Task ExecuteAsync_rejects_snapshot_binding_mismatch_and_disposes_key()
    {
        using var temporary = new TestTemporaryDirectory();
        var snapshot = CreateSnapshot(temporary);
        var key = new SensitiveBuffer(new byte[] { 9, 10, 11, 12 });
        var acquisition = CreateAcquisition(new string('f', 64), key);
        var service = new EphemeralAcquireAndMaterializeService(
            new FakeAcquisitionService(acquisition),
            new ThrowingMaterializer());

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => service.ExecuteAsync(
            snapshot,
            new KeyAcquisitionOptions("fake-profile", TimeSpan.FromSeconds(1)),
            new MaterializationOptions(temporary.GetPath("output")),
            CancellationToken.None));

        Assert.Contains("SnapshotId", exception.Message, StringComparison.Ordinal);
        Assert.Throws<ObjectDisposedException>(() => key.CopyTo(new byte[4]));
    }

    [Fact]
    public void VerifiedKeyAcquisition_rejects_mismatched_profile_and_clears_buffer()
    {
        using var key = new SensitiveBuffer(new byte[] { 1, 2, 3, 4 });

        Assert.Throws<InvalidDataException>(() => new VerifiedKeyAcquisition(
            "acquisition",
            new string('a', 64),
            "profile-a",
            [new DatabaseKeyBinding(new string('a', 64), "account", "group", "message.db", 0, "profile-b", "encryption-a", key)],
            DateTimeOffset.UtcNow));

        Assert.Throws<ObjectDisposedException>(() => key.CopyTo(new byte[4]));
    }

    [Fact]
    public async Task ExecuteAsync_rejects_materializer_result_from_the_wrong_snapshot_and_disposes_key()
    {
        using var temporary = new TestTemporaryDirectory();
        var snapshot = CreateSnapshot(temporary);
        var key = new SensitiveBuffer(new byte[] { 7, 8, 9, 10 });
        var service = new EphemeralAcquireAndMaterializeService(
            new FakeAcquisitionService(CreateAcquisition(snapshot.SnapshotId, key)),
            new MismatchedResultMaterializer());

        await Assert.ThrowsAsync<InvalidDataException>(() => service.ExecuteAsync(
            snapshot,
            new KeyAcquisitionOptions("fake-profile", TimeSpan.FromSeconds(1)),
            new MaterializationOptions(temporary.GetPath("output")),
            CancellationToken.None));

        Assert.Throws<ObjectDisposedException>(() => key.CopyTo(new byte[4]));
    }

    private static VerifiedRawSnapshot CreateSnapshot(TestTemporaryDirectory temporary)
    {
        var root = temporary.CreateDirectory("snapshot");
        var manifest = new SnapshotManifest(root, root, DateTimeOffset.UtcNow);
        return new VerifiedRawSnapshot(new RawSnapshot(manifest), DateTimeOffset.UtcNow);
    }

    private static VerifiedKeyAcquisition CreateAcquisition(string snapshotId, SensitiveBuffer key) =>
        new(
            "fake-acquisition",
            snapshotId,
            "fake-profile",
            [new DatabaseKeyBinding(snapshotId, "account", "group", "message/message_0.db", 0, "fake-profile", "fake-profile", key)],
            DateTimeOffset.UtcNow);

    private sealed class FakeAcquisitionService(VerifiedKeyAcquisition acquisition) : IKeyAcquisitionService
    {
        public Task<VerifiedKeyAcquisition> AcquireAsync(
            VerifiedRawSnapshot snapshot,
            KeyAcquisitionOptions options,
            CancellationToken cancellationToken) => Task.FromResult(acquisition);
    }

    private sealed class FakeEphemeralMaterializer(string outputRoot) : IEphemeralDatabaseMaterializer
    {
        public string BackendId => "fake-backend";

        public string EncryptionProfileId => "fake-profile";

        internal byte[]? ObservedKey { get; private set; }

        public Task<VerifiedMaterialization> MaterializeAsync(
            VerifiedRawSnapshot snapshot,
            VerifiedKeyAcquisition acquisition,
            MaterializationOptions options,
            CancellationToken cancellationToken)
        {
            ObservedKey = new byte[acquisition.Bindings[0].ProtectedKeyMaterial.Length];
            acquisition.Bindings[0].ProtectedKeyMaterial.CopyTo(ObservedKey);
            var manifestPath = Path.Combine(outputRoot, ".wechatvoice", "materialization-manifest.json");
            var result = new MaterializationResult(
                "fake-workspace",
                snapshot.SnapshotId,
                "fake-backend",
                "1",
                new string('0', 64),
                outputRoot,
                [],
                [],
                manifestPath);
            return Task.FromResult(new VerifiedMaterialization(result, DateTimeOffset.UtcNow));
        }
    }

    private sealed class ThrowingMaterializer : IEphemeralDatabaseMaterializer
    {
        public string BackendId => "fake-backend";

        public string EncryptionProfileId => "fake-profile";

        public Task<VerifiedMaterialization> MaterializeAsync(
            VerifiedRawSnapshot snapshot,
            VerifiedKeyAcquisition acquisition,
            MaterializationOptions options,
            CancellationToken cancellationToken) => throw new InvalidDataException("fake materialization failure");
    }

    private sealed class MismatchedResultMaterializer : IEphemeralDatabaseMaterializer
    {
        public string BackendId => "fake-backend";

        public string EncryptionProfileId => "fake-profile";

        public Task<VerifiedMaterialization> MaterializeAsync(
            VerifiedRawSnapshot snapshot,
            VerifiedKeyAcquisition acquisition,
            MaterializationOptions options,
            CancellationToken cancellationToken)
        {
            var result = new MaterializationResult(
                "fake-workspace",
                new string('f', 64),
                "fake-backend",
                "1",
                new string('0', 64),
                options.OutputDirectory,
                [],
                [],
                Path.Combine(options.OutputDirectory, ".wechatvoice", "materialization-manifest.json"));
            return Task.FromResult(new VerifiedMaterialization(result, DateTimeOffset.UtcNow));
        }
    }
}
