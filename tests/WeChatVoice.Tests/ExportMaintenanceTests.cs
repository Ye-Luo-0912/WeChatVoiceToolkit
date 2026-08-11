using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using WeChatVoice.Core.Errors;
using WeChatVoice.Core.Models;
using WeChatVoice.Core.Ports;
using WeChatVoice.Infrastructure.Export;
using WeChatVoice.Workflows.Workflows;

namespace WeChatVoice.Tests;

public sealed class ExportMaintenanceTests
{
    [Fact]
    public async Task Verify_maps_a_cross_process_lock_conflict_to_operation_busy()
    {
        using var temporary = new TestTemporaryDirectory();
        var exportRoot = temporary.GetPath("export");
        Directory.CreateDirectory(exportRoot);
        await using var held = await ExportRootLock.AcquireAsync(
            exportRoot,
            ExportRootLockMode.Exclusive,
            "held-operation",
            runId: null,
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<WeChatVoice.Core.Errors.AppFailureException>(() =>
            new ExportVerificationService().VerifyAsync(exportRoot, null, CancellationToken.None));

        Assert.Equal(ErrorCode.OperationBusy, exception.Code);
    }

    [Fact]
    public async Task VerifyAsync_accepts_a_committed_export_and_validates_the_artifact_index()
    {
        using var temporary = new TestTemporaryDirectory();
        var export = await CreateCommittedExportAsync(temporary);

        var result = await new ExportVerificationService().VerifyAsync(
            export.Root,
            runId: null,
            CancellationToken.None);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Issues.Select(issue => issue.Code)));
        Assert.Equal(1, result.VerifiedOriginalCount);
        Assert.True(File.Exists(Path.Combine(export.Root, "artifact-index.jsonl")));
    }

    [Fact]
    public async Task VerifyAsync_rejects_a_modified_silk_even_when_the_length_is_unchanged()
    {
        using var temporary = new TestTemporaryDirectory();
        var export = await CreateCommittedExportAsync(temporary);
        var silkPath = Path.Combine(export.Root, export.Entry.OriginalPath.Replace('/', Path.DirectorySeparatorChar));
        var originalWriteTime = File.GetLastWriteTimeUtc(silkPath);
        await File.WriteAllBytesAsync(silkPath, [9, 8, 7, 6]);
        File.SetLastWriteTimeUtc(silkPath, originalWriteTime);

        var result = await new ExportVerificationService().VerifyAsync(export.Root, null, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == "artifact-hash-mismatch");
    }

    [Fact]
    public async Task RepairAsync_rebuilds_only_derived_files_and_keeps_original_silk_bytes()
    {
        using var temporary = new TestTemporaryDirectory();
        var export = await CreateCommittedExportAsync(temporary);
        var silkPath = Path.Combine(export.Root, export.Entry.OriginalPath.Replace('/', Path.DirectorySeparatorChar));
        var originalBytes = await File.ReadAllBytesAsync(silkPath);
        await File.WriteAllTextAsync(Path.Combine(export.Root, "dataset.csv"), "broken\n");
        File.Delete(Path.Combine(export.Root, "artifact-index.jsonl"));

        var repaired = await new ExportVerificationService().RepairAsync(export.Root, null, CancellationToken.None);

        Assert.True(repaired.Verification.IsValid, string.Join(Environment.NewLine, repaired.Verification.Issues.Select(issue => issue.Code)));
        Assert.False(repaired.OriginalArtifactsChanged);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(silkPath));
        Assert.True(File.Exists(Path.Combine(export.Root, "dataset.csv")));
        Assert.True(File.Exists(Path.Combine(export.Root, "artifact-index.jsonl")));
    }

    [Fact]
    public async Task Dataset_curation_starts_unselected_and_rejects_multiple_duplicate_representatives()
    {
        using var temporary = new TestTemporaryDirectory();
        var entries = new[]
        {
            CreateEntry("one", "original/one.silk", new string('a', 64), 100, 3, VoiceDirection.Incoming),
            CreateEntry("two", "original/two.silk", new string('a', 64), null, 4, VoiceDirection.Incoming),
            CreateEntry("three", "original/three.silk", new string('b', 64), 200, 5, VoiceDirection.Outgoing),
        };
        var manifestPath = Path.Combine(temporary.RootPath, "export", "manifest.private.json");
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        await WriteManifestAsync(manifestPath, entries);

        var workflow = new DatasetCurationWorkflow();
        var first = await workflow.RunAsync(
            new DatasetCurationRequest(Path.GetDirectoryName(manifestPath)!),
            NewContext(),
            CancellationToken.None);

        Assert.DoesNotContain(first.Items, item => item.IsSelected);
        Assert.Equal(1, first.Items.Count(item => item.PassesFilters));
        Assert.Equal(
            first.Items.Single(item => item.ItemId == ExportItemIdentity.ComputeItemId(entries[0])).DuplicateGroupId,
            first.Items.Single(item => item.ItemId == ExportItemIdentity.ComputeItemId(entries[1])).DuplicateGroupId);
        Assert.Equal(0, first.SelectedDurationMs);
        Assert.Equal(TrainingEligibility.Rejected, first.Items.Single(item => item.DurationMs is null).TrainingEligibility);
        Assert.Equal(first.Profile.SelectionFingerprint, first.SelectionFingerprint);

        var firstId = ExportItemIdentity.ComputeItemId(entries[0]);
        var secondId = ExportItemIdentity.ComputeItemId(entries[1]);
        await Assert.ThrowsAsync<WeChatVoice.Core.Errors.AppFailureException>(() => workflow.RunAsync(
            new DatasetCurationRequest(
                Path.GetDirectoryName(manifestPath)!,
                SelectedItemIds: [firstId, secondId],
                DuplicateRepresentativeItemIds: [firstId, secondId]),
            NewContext(),
            CancellationToken.None));

        var selected = await workflow.RunAsync(
            new DatasetCurationRequest(
                Path.GetDirectoryName(manifestPath)!,
                SelectedItemIds: [firstId],
                DuplicateRepresentativeItemIds: [firstId]),
            NewContext(),
            CancellationToken.None);
        Assert.Single(selected.Items, item => item.IsSelected);
        Assert.Equal(100, selected.SelectedDurationMs);
        Assert.Equal(TrainingEligibility.Eligible, selected.Items.Single(item => item.IsSelected).TrainingEligibility);

        var repeated = await workflow.RunAsync(
            new DatasetCurationRequest(
                Path.GetDirectoryName(manifestPath)!,
                SelectedItemIds: [firstId],
                DuplicateRepresentativeItemIds: [firstId]),
            NewContext(),
            CancellationToken.None);
        Assert.Equal(selected.SelectionFingerprint, repeated.SelectionFingerprint);
        Assert.Equal(selected.Profile.SelectionFingerprint, repeated.Profile.SelectionFingerprint);

        var profileStore = new DatasetSelectionProfileStore();
        await profileStore.WriteAsync(Path.GetDirectoryName(manifestPath)!, selected.Profile, CancellationToken.None);
        var loadedProfile = await profileStore.ReadAsync(Path.GetDirectoryName(manifestPath)!, CancellationToken.None);
        Assert.Equal(selected.Profile.SelectionFingerprint, loadedProfile.SelectionFingerprint);
    }

    [Fact]
    public async Task Dataset_curation_requires_the_selection_profile_manifest_hash()
    {
        using var temporary = new TestTemporaryDirectory();
        var entry = CreateEntry("one", "original/one.silk", "hash", 100, 3, VoiceDirection.Incoming);
        var manifestPath = Path.Combine(temporary.RootPath, "export", "manifest.private.json");
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        await WriteManifestAsync(manifestPath, [entry]);

        await Assert.ThrowsAsync<WeChatVoice.Core.Errors.AppFailureException>(() => new DatasetCurationWorkflow().RunAsync(
            new DatasetCurationRequest(
                Path.GetDirectoryName(manifestPath)!,
                ExpectedManifestSha256: new string('0', 64)),
            NewContext(),
            CancellationToken.None));
    }

    [Fact]
    public async Task Dataset_build_verifies_all_derived_outputs_and_reuses_a_verified_build()
    {
        using var temporary = new TestTemporaryDirectory();
        var export = await CreateCommittedExportAsync(temporary);
        var curation = new DatasetCurationWorkflow();
        var manifest = await ReadPrivateManifestAsync(export.Root);
        var itemId = ExportItemIdentity.ComputeItemId(export.Entry, manifest.DatasetNamespaceKey);
        var curated = await curation.RunAsync(
            new DatasetCurationRequest(export.Root, SelectedItemIds: [itemId], DuplicateRepresentativeItemIds: [itemId]),
            NewContext(),
            CancellationToken.None);
        await curation.SaveProfileAsync(export.Root, curated.Profile, NewContext(), CancellationToken.None);

        var first = await curation.BuildDatasetAsync(
            new DatasetBuildRequest(export.Root),
            NewContext(),
            CancellationToken.None);
        Assert.Equal(1, first.ItemCount);
        Assert.True(File.Exists(Path.Combine(first.OutputDirectory, "dataset.json")));
        Assert.True(File.Exists(Path.Combine(first.OutputDirectory, "dataset.csv")));
        Assert.True(File.Exists(Path.Combine(first.OutputDirectory, "selection-profile.json")));
        Assert.False(string.IsNullOrWhiteSpace(first.ManifestPath));

        var firstBuildHash = await File.ReadAllBytesAsync(first.BuildManifestPath);
        var second = await curation.BuildDatasetAsync(
            new DatasetBuildRequest(export.Root),
            NewContext(),
            CancellationToken.None);
        Assert.Equal(first.OutputDirectory, second.OutputDirectory);
        Assert.Equal(first.ManifestPath, second.ManifestPath);
        Assert.Equal(firstBuildHash, await File.ReadAllBytesAsync(second.BuildManifestPath));

        await File.AppendAllTextAsync(Path.Combine(first.OutputDirectory, "dataset.csv"), "tampered\n");
        await Assert.ThrowsAsync<WeChatVoice.Core.Errors.AppFailureException>(() => curation.BuildDatasetAsync(
            new DatasetBuildRequest(export.Root),
            NewContext(),
            CancellationToken.None));
    }

    [Fact]
    public async Task Dataset_build_is_an_independent_copy_and_reuses_same_semantic_profile()
    {
        using var temporary = new TestTemporaryDirectory();
        var export = await CreateCommittedExportAsync(temporary);
        var curation = new DatasetCurationWorkflow();
        var manifest = await ReadPrivateManifestAsync(export.Root);
        var itemId = ExportItemIdentity.ComputeItemId(export.Entry, manifest.DatasetNamespaceKey);
        var curated = await curation.RunAsync(
            new DatasetCurationRequest(export.Root, SelectedItemIds: [itemId], DuplicateRepresentativeItemIds: [itemId]),
            NewContext(),
            CancellationToken.None);
        await curation.SaveProfileAsync(export.Root, curated.Profile, NewContext(), CancellationToken.None);

        var first = await curation.BuildDatasetAsync(
            new DatasetBuildRequest(export.Root),
            NewContext(),
            CancellationToken.None);
        var sourcePath = Path.Combine(export.Root, export.Entry.OriginalPath.Replace('/', Path.DirectorySeparatorChar));
        var sourceBytes = await File.ReadAllBytesAsync(sourcePath);
        var datasetAudioPath = Path.Combine(first.OutputDirectory, "audio", itemId + ".silk");
        var datasetBytes = await File.ReadAllBytesAsync(datasetAudioPath);
        await File.WriteAllBytesAsync(datasetAudioPath, [9, 8, 7]);
        Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(sourcePath));
        await File.WriteAllBytesAsync(datasetAudioPath, datasetBytes);

        // Rebuild a semantically identical profile with a different audit
        // timestamp. The existing dataset identity remains reusable.
        var rewrittenProfile = new DatasetSelectionProfile(
            curated.Profile.ManifestSha256,
            curated.Profile.RunId,
            curated.Profile.Filters,
            curated.Profile.SelectedItemIds,
            curated.Profile.DuplicateRepresentativeItemIds,
            UpdatedAtUtc: curated.Profile.UpdatedAtUtc.AddDays(1));
        await curation.SaveProfileAsync(export.Root, rewrittenProfile, NewContext(), CancellationToken.None);
        var reused = await curation.BuildDatasetAsync(
            new DatasetBuildRequest(export.Root),
            NewContext(),
            CancellationToken.None);
        Assert.Equal(first.OutputDirectory, reused.OutputDirectory);
        Assert.Equal(DatasetLinkMode.VerifiedCopy, reused.LinkMode);
    }

    [Fact]
    public async Task Dataset_delete_uses_the_dataset_profile_after_the_current_profile_changes()
    {
        using var temporary = new TestTemporaryDirectory();
        var export = await CreateCommittedExportAsync(temporary);
        var curation = new DatasetCurationWorkflow();
        var manifest = await ReadPrivateManifestAsync(export.Root);
        var itemId = ExportItemIdentity.ComputeItemId(export.Entry, manifest.DatasetNamespaceKey);
        var curated = await curation.RunAsync(
            new DatasetCurationRequest(export.Root, SelectedItemIds: [itemId], DuplicateRepresentativeItemIds: [itemId]),
            NewContext(),
            CancellationToken.None);
        await curation.SaveProfileAsync(export.Root, curated.Profile, NewContext(), CancellationToken.None);
        var built = await curation.BuildDatasetAsync(new DatasetBuildRequest(export.Root), NewContext(), CancellationToken.None);

        var changed = new DatasetSelectionProfile(
            curated.Profile.ManifestSha256,
            curated.Profile.RunId,
            curated.Profile.Filters,
            SelectedItemIds: [],
            DuplicateRepresentativeItemIds: [],
            UpdatedAtUtc: curated.Profile.UpdatedAtUtc.AddMinutes(1));
        await curation.SaveProfileAsync(export.Root, changed, NewContext(), CancellationToken.None);

        var deleted = await curation.DeleteDatasetAsync(
            new DatasetDeleteRequest(
                export.Root,
                built.OutputDirectory,
                curated.Profile.SelectionFingerprint,
                Confirmed: true),
            NewContext(),
            CancellationToken.None);

        Assert.Equal(built.OutputDirectory, deleted.OutputDirectory);
        Assert.False(Directory.Exists(built.OutputDirectory));
        Assert.True(File.Exists(Path.Combine(export.Root, export.Entry.OriginalPath.Replace('/', Path.DirectorySeparatorChar))));
    }

    [Fact]
    public async Task Dataset_wav_build_creates_derived_wav_and_records_audio_profile_fingerprint()
    {
        using var temporary = new TestTemporaryDirectory();
        var export = await CreateCommittedExportAsync(temporary);
        var curation = new DatasetCurationWorkflow(
            datasetBuildService: new DatasetBuildService(new FakeDecoderFactory()));
        var manifest = await ReadPrivateManifestAsync(export.Root);
        var itemId = ExportItemIdentity.ComputeItemId(export.Entry, manifest.DatasetNamespaceKey);
        var curated = await curation.RunAsync(
            new DatasetCurationRequest(export.Root, SelectedItemIds: [itemId], DuplicateRepresentativeItemIds: [itemId]),
            NewContext(),
            CancellationToken.None);
        await curation.SaveProfileAsync(export.Root, curated.Profile, NewContext(), CancellationToken.None);

        var profile = new AudioBuildProfile(SampleRate: 24000);
        var built = await curation.BuildDatasetAsync(
            new DatasetBuildRequest(export.Root, AudioProfile: profile),
            NewContext(),
            CancellationToken.None);

        Assert.Equal(1, built.ItemCount);
        Assert.False(string.IsNullOrWhiteSpace(built.BuildFingerprint));
        Assert.Equal(profile.ProfileFingerprint, built.AudioProfileFingerprint);
        Assert.Equal(FakeDecoderFactory.DecoderIdentity, built.DecoderIdentity);
        var wavPath = Path.Combine(built.OutputDirectory, "audio", itemId + ".wav");
        Assert.True(File.Exists(wavPath));
        Assert.False(File.Exists(Path.Combine(built.OutputDirectory, "audio", itemId + ".silk")));
        var wavBytes = await File.ReadAllBytesAsync(wavPath);
        Assert.Equal(FakeDecoderFactory.WavBytes, wavBytes);
    }

    [Fact]
    public async Task Dataset_wav_build_reuses_output_when_selection_and_audio_profile_are_unchanged()
    {
        using var temporary = new TestTemporaryDirectory();
        var export = await CreateCommittedExportAsync(temporary);
        var curation = new DatasetCurationWorkflow(
            datasetBuildService: new DatasetBuildService(new FakeDecoderFactory()));
        var manifest = await ReadPrivateManifestAsync(export.Root);
        var itemId = ExportItemIdentity.ComputeItemId(export.Entry, manifest.DatasetNamespaceKey);
        var curated = await curation.RunAsync(
            new DatasetCurationRequest(export.Root, SelectedItemIds: [itemId], DuplicateRepresentativeItemIds: [itemId]),
            NewContext(),
            CancellationToken.None);
        await curation.SaveProfileAsync(export.Root, curated.Profile, NewContext(), CancellationToken.None);

        var profile = new AudioBuildProfile(SampleRate: 24000);
        var first = await curation.BuildDatasetAsync(
            new DatasetBuildRequest(export.Root, AudioProfile: profile),
            NewContext(),
            CancellationToken.None);
        var firstBuildHash = await File.ReadAllBytesAsync(first.BuildManifestPath);
        var second = await curation.BuildDatasetAsync(
            new DatasetBuildRequest(export.Root, AudioProfile: profile),
            NewContext(),
            CancellationToken.None);

        Assert.Equal(first.OutputDirectory, second.OutputDirectory);
        Assert.Equal(firstBuildHash, await File.ReadAllBytesAsync(second.BuildManifestPath));

        // A differing audio profile must not overwrite the earlier build.
        var differentProfile = new AudioBuildProfile(SampleRate: 16000);
        var changed = await curation.BuildDatasetAsync(
            new DatasetBuildRequest(export.Root, AudioProfile: differentProfile),
            NewContext(),
            CancellationToken.None);
        Assert.NotEqual(first.OutputDirectory, changed.OutputDirectory);
        Assert.Equal(differentProfile.ProfileFingerprint, changed.AudioProfileFingerprint);
    }

    [Fact]
    public async Task Dataset_wav_verify_and_repair_preserve_the_derived_audio_identity()
    {
        using var temporary = new TestTemporaryDirectory();
        var export = await CreateCommittedExportAsync(temporary);
        var curation = new DatasetCurationWorkflow(
            datasetBuildService: new DatasetBuildService(new FakeDecoderFactory()));
        var manifest = await ReadPrivateManifestAsync(export.Root);
        var itemId = ExportItemIdentity.ComputeItemId(export.Entry, manifest.DatasetNamespaceKey);
        var curated = await curation.RunAsync(
            new DatasetCurationRequest(export.Root, SelectedItemIds: [itemId], DuplicateRepresentativeItemIds: [itemId]),
            NewContext(),
            CancellationToken.None);
        await curation.SaveProfileAsync(export.Root, curated.Profile, NewContext(), CancellationToken.None);

        var profile = new AudioBuildProfile(SampleRate: 24000);
        var built = await curation.BuildDatasetAsync(
            new DatasetBuildRequest(export.Root, AudioProfile: profile),
            NewContext(),
            CancellationToken.None);

        // Corrupt only the derived metadata; the WAV itself must survive.
        await File.WriteAllTextAsync(Path.Combine(built.OutputDirectory, "dataset.csv"), "broken\n");
        var repaired = await curation.RepairDatasetAsync(
            new DatasetBuildRepairRequest(export.Root, built.OutputDirectory),
            NewContext(),
            CancellationToken.None);
        Assert.True(repaired.IsValid, string.Join(Environment.NewLine, repaired.Issues.Select(issue => issue.Code)));
        Assert.Equal(profile.ProfileFingerprint, repaired.AudioProfileFingerprint);
        Assert.Equal(FakeDecoderFactory.DecoderIdentity, repaired.DecoderIdentity);
        Assert.True(File.Exists(Path.Combine(built.OutputDirectory, "audio", itemId + ".wav")));

        // Deleting the WAV build must not touch the source SILK export.
        await curation.DeleteDatasetAsync(
            new DatasetDeleteRequest(
                export.Root,
                built.OutputDirectory,
                curated.Profile.SelectionFingerprint,
                Confirmed: true),
            NewContext(),
            CancellationToken.None);
        Assert.False(Directory.Exists(built.OutputDirectory));
        Assert.True(File.Exists(Path.Combine(export.Root, export.Entry.OriginalPath.Replace('/', Path.DirectorySeparatorChar))));
    }

    private sealed class FakeDecoderFactory : IVoiceDecoderFactory
    {
        public const string DecoderIdentity = "fake-decoder-v1";
        public static readonly byte[] WavBytes = BuildWav();

        public IVoiceDecoder? Create(int sampleRate)
            => new FixedWavDecoder(sampleRate);

        private static byte[] BuildWav()
        {
            var dataBytes = 480;
            var wav = new byte[44 + dataBytes];
            "RIFF"u8.CopyTo(wav);
            BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(4), (uint)(wav.Length - 8));
            "WAVE"u8.CopyTo(wav.AsSpan(8));
            "fmt "u8.CopyTo(wav.AsSpan(12));
            BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(16), 16);
            BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(20), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(22), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(24), 24000);
            BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(28), 48000);
            BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(32), 2);
            BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(34), 16);
            "data"u8.CopyTo(wav.AsSpan(36));
            BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(40), (uint)dataBytes);
            return wav;
        }
    }

    private sealed class FixedWavDecoder(int requestedSampleRate) : IVoiceDecoder, IVoiceDecoderIdentity
    {
        public string DecoderIdentity => FakeDecoderFactory.DecoderIdentity;

        public async Task DecodeAsync(Stream input, Stream output, CancellationToken cancellationToken)
        {
            var wav = FakeDecoderFactory.WavBytes;
            BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(24), (uint)requestedSampleRate);
            BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(28), (uint)(requestedSampleRate * 2));
            await output.WriteAsync(wav, cancellationToken);
        }
    }

    [Fact]
    public async Task Dataset_build_verify_and_repair_rebuild_only_derived_metadata()
    {
        using var temporary = new TestTemporaryDirectory();
        var export = await CreateCommittedExportAsync(temporary);
        var curation = new DatasetCurationWorkflow();
        var manifest = await ReadPrivateManifestAsync(export.Root);
        var itemId = ExportItemIdentity.ComputeItemId(export.Entry, manifest.DatasetNamespaceKey);
        var curated = await curation.RunAsync(
            new DatasetCurationRequest(export.Root, SelectedItemIds: [itemId], DuplicateRepresentativeItemIds: [itemId]),
            NewContext(),
            CancellationToken.None);
        await curation.SaveProfileAsync(export.Root, curated.Profile, NewContext(), CancellationToken.None);

        var built = await curation.BuildDatasetAsync(
            new DatasetBuildRequest(export.Root),
            NewContext(),
            CancellationToken.None);
        var silkPath = Path.Combine(built.OutputDirectory, "audio", itemId + ".silk");
        var silkBytes = await File.ReadAllBytesAsync(silkPath);
        var verified = await curation.VerifyDatasetAsync(
            new DatasetBuildRequest(export.Root, OutputDirectory: built.OutputDirectory),
            NewContext(),
            CancellationToken.None);
        Assert.True(verified.IsValid, string.Join(Environment.NewLine, verified.Issues.Select(issue => issue.Code)));

        await File.WriteAllTextAsync(Path.Combine(built.OutputDirectory, "dataset.csv"), "broken\n");
        var invalid = await curation.VerifyDatasetAsync(
            new DatasetBuildRequest(export.Root, OutputDirectory: built.OutputDirectory),
            NewContext(),
            CancellationToken.None);
        Assert.False(invalid.IsValid);

        var repaired = await curation.RepairDatasetAsync(
            new DatasetBuildRepairRequest(export.Root, built.OutputDirectory),
            NewContext(),
            CancellationToken.None);
        Assert.True(repaired.IsValid, string.Join(Environment.NewLine, repaired.Issues.Select(issue => issue.Code)));
        Assert.Equal(silkBytes, await File.ReadAllBytesAsync(silkPath));
    }

    [Fact]
    public async Task Dataset_metadata_transaction_recovers_after_a_partial_publish()
    {
        using var temporary = new TestTemporaryDirectory();
        var export = await CreateCommittedExportAsync(temporary);
        var curation = new DatasetCurationWorkflow();
        var manifest = await ReadPrivateManifestAsync(export.Root);
        var itemId = ExportItemIdentity.ComputeItemId(export.Entry, manifest.DatasetNamespaceKey);
        var curated = await curation.RunAsync(
            new DatasetCurationRequest(export.Root, SelectedItemIds: [itemId], DuplicateRepresentativeItemIds: [itemId]),
            NewContext(),
            CancellationToken.None);
        await curation.SaveProfileAsync(export.Root, curated.Profile, NewContext(), CancellationToken.None);
        var built = await curation.BuildDatasetAsync(new DatasetBuildRequest(export.Root), NewContext(), CancellationToken.None);

        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        };
        var descriptorPath = Path.Combine(built.OutputDirectory, "dataset-metadata-commit.json");
        var descriptor = JsonSerializer.Deserialize<DatasetMetadataCommitDescriptor>(
            await File.ReadAllTextAsync(descriptorPath),
            jsonOptions);
        Assert.NotNull(descriptor);
        var stagingName = ".dataset-metadata.staging-recovery";
        var stagingRoot = Path.Combine(built.OutputDirectory, stagingName);
        Directory.CreateDirectory(stagingRoot);
        foreach (var fileName in new[] { "selection-profile.json", "dataset.json", "dataset.csv", "build-manifest.json", "dataset-metadata-commit.json" })
        {
            File.Copy(Path.Combine(built.OutputDirectory, fileName), Path.Combine(stagingRoot, fileName));
        }

        await File.WriteAllTextAsync(Path.Combine(built.OutputDirectory, "dataset.csv"), "partial\n");
        var transaction = new DatasetMetadataTransactionDocument(
            descriptor!.TransactionId,
            stagingName,
            DatasetMetadataTransactionState.Publishing,
            DateTimeOffset.UtcNow,
            descriptor,
            Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(stagingRoot, "dataset-metadata-commit.json")))).ToLowerInvariant());
        await File.WriteAllTextAsync(
            Path.Combine(built.OutputDirectory, "dataset-metadata.transaction.json"),
            JsonSerializer.Serialize(transaction, jsonOptions));

        var verified = await curation.VerifyDatasetAsync(
            new DatasetBuildRequest(export.Root, OutputDirectory: built.OutputDirectory),
            NewContext(),
            CancellationToken.None);

        Assert.True(verified.IsValid, string.Join(Environment.NewLine, verified.Issues.Select(issue => issue.Code)));
        Assert.False(File.Exists(Path.Combine(built.OutputDirectory, "dataset-metadata.transaction.json")));
        Assert.False(Directory.Exists(stagingRoot));
    }

    private static async Task<(string Root, VoiceExportEntry Entry)> CreateCommittedExportAsync(TestTemporaryDirectory temporary)
    {
        var root = temporary.GetPath("export");
        var bytes = new byte[] { 1, 2, 3, 4 };
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var record = new VoiceRecord(
            "message",
            "conversation",
            DateTimeOffset.UtcNow,
            VoiceDirection.Incoming,
            new VoicePayloadLocator("media", 0, "blob"),
            SnapshotId: "snapshot",
            AdapterId: "adapter",
            AccountId: "account",
            DataSetId: "dataset",
            AdapterVersion: "1",
            DatabaseFingerprints: ["database"],
            AdapterFamily: "adapter",
            AccountStableId: "account",
            ConversationStableId: "conversation",
            MessagePrimaryKey: "message",
            MediaPrimaryKey: "media:0:blob",
            PayloadSha256: hash,
            PayloadByteLength: bytes.Length);

        VoiceExportEntry entry;
        var store = new FileSystemVoiceExportStore(root);
        await using (var item = await store.BeginItemAsync(record, ExistingArtifactPolicy.Replace, CancellationToken.None))
        {
            await using (var output = await item.OpenOriginalWriteAsync(CancellationToken.None))
            {
                await output.WriteAsync(bytes);
            }

            var artifact = await item.CommitOriginalAsync(CancellationToken.None);
            entry = new VoiceExportEntry(
                record.MessageId,
                record.ConversationId,
                record.OccurredAtUtc,
                record.Direction,
                artifact.RelativePath,
                artifact.ByteLength,
                artifact.Sha256,
                null,
                record.SourceStableKey,
                SourceDatabase: "messages.db",
                ShardId: "0",
                DurationMs: 100);
        }

        var context = new VoiceCatalogContext("dataset", "adapter", "1", "account", ["database"]);
        await using (var journal = await store.BeginRunAsync(
            new VoiceExportRunContext("run-test", context, DateTimeOffset.UtcNow),
            CancellationToken.None))
        {
            await journal.AppendAsync(new VoiceExportJournalEvent("run-started", "run-test", DateTimeOffset.UtcNow, Context: context), CancellationToken.None);
            await journal.AppendAsync(new VoiceExportJournalEvent("item-committed", "run-test", DateTimeOffset.UtcNow, entry.MessageId, Entry: entry), CancellationToken.None);
            await journal.AppendAsync(new VoiceExportJournalEvent("processing-completed", "run-test", DateTimeOffset.UtcNow, Context: context), CancellationToken.None);
            await journal.FinalizeAsync(new VoiceExportManifest(DateTimeOffset.UtcNow, RunId: "run-test"), CancellationToken.None);
        }

        return (root, entry);
    }

    private static async Task<VoiceExportManifest> ReadPrivateManifestAsync(string root)
    {
        await using var stream = File.OpenRead(Path.Combine(root, "manifest.private.json"));
        return await JsonSerializer.DeserializeAsync<VoiceExportManifest>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        }) ?? throw new InvalidDataException("The private manifest is empty.");
    }

    private static VoiceExportEntry CreateEntry(
        string messageId,
        string path,
        string hash,
        long? duration,
        long length,
        VoiceDirection direction)
        => new(
            messageId,
            "conversation",
            DateTimeOffset.UtcNow,
            direction,
            path,
            length,
            hash,
            null,
            SourceStableKey: "adapter|account|conversation|" + messageId + "|media:" + messageId,
            DurationMs: duration);

    private static async Task WriteManifestAsync(string path, IReadOnlyList<VoiceExportEntry> entries)
    {
        var manifest = new VoiceExportManifest(DateTimeOffset.UtcNow, entries, RunId: "curation-run");
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(manifest, options));
    }

    private static WorkflowContext NewContext()
        => new(new TestAccountConfirmation());

    private sealed class TestAccountConfirmation : WeChatVoice.Core.Ports.IAccountConfirmation
    {
        public Task<AccountConfirmation> ConfirmAsync(AccountIdentityReport report, CancellationToken cancellationToken)
            => Task.FromResult(new AccountConfirmation(true, report.AccountCandidate));
    }
}
