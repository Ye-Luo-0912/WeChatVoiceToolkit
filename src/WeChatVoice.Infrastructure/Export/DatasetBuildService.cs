using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using WeChatVoice.Core.Errors;
using WeChatVoice.Core.Models;
using WeChatVoice.Core.Ports;
using WeChatVoice.Infrastructure.Audio;
using WeChatVoice.Infrastructure.Serialization;

namespace WeChatVoice.Infrastructure.Export;

/// <summary>
/// Builds a derived training dataset from a verified export and an explicit
/// selection profile. VerifiedCopy is the default and keeps the dataset
/// independent from the original export; LinkedView is an explicit advanced
/// mode and is never presented as a portable independent copy.
/// An <see cref="AudioBuildProfile"/> switches the build to a derived WAV
/// training set (SILK decoded through the configured decoder); the original
/// SILK files always remain the source of truth.
/// </summary>
public sealed class DatasetBuildService
{
    private const string DatasetMetadataDescriptorFileName = "dataset-metadata-commit.json";
    private const string DatasetMetadataTransactionFileName = "dataset-metadata.transaction.json";
    private static readonly string[] DatasetMetadataFileNames =
    [
        "selection-profile.json",
        "dataset.json",
        "dataset.csv",
        "build-manifest.json",
    ];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly IVoiceDecoderFactory? _decoderFactory;
    private readonly FfmpegWavNormalizer? _ffmpegNormalizer;

    public DatasetBuildService(IVoiceDecoderFactory? decoderFactory = null, FfmpegWavNormalizer? ffmpegNormalizer = null)
    {
        _decoderFactory = decoderFactory;
        _ffmpegNormalizer = ffmpegNormalizer;
    }

    public async Task<DatasetBuildResult> BuildAsync(
        DatasetBuildRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var exportRoot = Path.GetFullPath(request.ExportDirectory);
        if (!Directory.Exists(exportRoot))
        {
            throw new AppFailureException(ErrorCode.InvalidRequest, "The export directory does not exist.");
        }

        await using var rootLock = await ExportRootLock.AcquireForOperationAsync(
            exportRoot,
            ExportRootLockMode.Exclusive,
            Guid.NewGuid().ToString("N"),
            runId: null,
            cancellationToken).ConfigureAwait(false);
        var exportStore = new FileSystemVoiceExportStore(exportRoot);
        await exportStore.RecoverPendingTransactionsUnderLockAsync(cancellationToken, rootLock).ConfigureAwait(false);
        await RecoverDatasetMetadataTransactionsUnderLockAsync(exportRoot, cancellationToken).ConfigureAwait(false);
        var inputs = await LoadInputsAsync(
            exportRoot,
            request.ManifestPath,
            request.ProfilePath,
            cancellationToken).ConfigureAwait(false);
        var manifestPath = inputs.ManifestPath;
        var profilePath = inputs.ProfilePath;
        var manifestSha256 = inputs.ManifestSha256;
        var profileSha256 = inputs.ProfileSha256;
        var manifest = inputs.Manifest;
        var profile = inputs.Profile;
        var entries = inputs.Entries;
        var selectedIds = profile.SelectedItemIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var audioProfile = request.AudioProfile;
        var buildFingerprint = ComputeBuildFingerprint(profile, audioProfile);

        var outputRoot = Path.GetFullPath(request.OutputDirectory ?? Path.Combine(exportRoot, "datasets", buildFingerprint));
        EnsureUnderRoot(exportRoot, outputRoot);
        if (Directory.Exists(outputRoot)
            && (File.GetAttributes(outputRoot) & FileAttributes.ReparsePoint) != 0)
        {
            throw new AppFailureException(ErrorCode.InvalidRequest, "The curated dataset output cannot be a reparse point.");
        }
        var existing = await TryVerifyExistingAsync(
            outputRoot,
            profile,
            manifestPath,
            manifestSha256,
            profileSha256,
            request.LinkMode,
            buildFingerprint,
            audioProfile?.ProfileFingerprint,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        if (Directory.Exists(outputRoot))
        {
            throw new AppFailureException(ErrorCode.InvalidRequest, "The curated dataset output already exists but failed verification.");
        }

        var parent = Path.GetDirectoryName(outputRoot) ?? throw new InvalidDataException("The dataset output has no parent directory.");
        Directory.CreateDirectory(parent);
        CleanupStagingDirectories(parent, Path.GetFileName(outputRoot));
        var stagingRoot = Path.Combine(parent, "." + Path.GetFileName(outputRoot) + ".staging-" + Guid.NewGuid().ToString("N"));
        var usedHardLinks = false;
        IVoiceDecoder? decoder = null;
        string? decoderIdentity = null;
        try
        {
            if (audioProfile is not null)
            {
                decoder = _decoderFactory?.Create(audioProfile.SampleRate)
                    ?? throw new AppFailureException(ErrorCode.InvalidRequest, "No SILK decoder is configured; cannot build the WAV training set.");
                decoderIdentity = decoder is IVoiceDecoderIdentity identity ? identity.DecoderIdentity : null;
            }

            Directory.CreateDirectory(Path.Combine(stagingRoot, "audio"));
            var datasetItems = new List<VoiceDatasetEntry>(selectedIds.Count);
            var buildItems = new List<DatasetBuildItem>(selectedIds.Count);
            foreach (var itemId in profile.SelectedItemIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = entries[itemId];
                if (!IsTrainingEligible(entry, profile.Filters))
                {
                    throw new AppFailureException(ErrorCode.InvalidRequest, "The selection profile contains an item that is not explicitly training-eligible and selected.");
                }

                var sourcePath = ResolveArtifactPath(exportRoot, entry.OriginalPath);
                if (audioProfile is not null)
                {
                    var relativeWavPath = "audio/" + itemId + ".wav";
                    var wavDestinationPath = ExportPathSafety.CombineUnderRoot(stagingRoot, relativeWavPath);
                    var durationMs = await DecodeToWavAsync(decoder!, sourcePath, wavDestinationPath, entry, audioProfile, _ffmpegNormalizer, cancellationToken).ConfigureAwait(false);
                    var wavMetadata = await FileHashing.ComputeMetadataAsync(wavDestinationPath, cancellationToken).ConfigureAwait(false);
                    datasetItems.Add(new VoiceDatasetEntry(
                        itemId,
                        relativeWavPath,
                        wavMetadata.Sha256,
                        wavMetadata.ByteLength,
                        durationMs.DurationMs,
                        MergeQualityFlags(entry.QualityFlags, durationMs.QualityFlags),
                        TrainingEligibility.Eligible,
                        Selected: true));
                    buildItems.Add(new DatasetBuildItem(itemId, relativeWavPath, wavMetadata.Sha256, wavMetadata.ByteLength, durationMs.DurationMs));
                    continue;
                }

                var relativeAudioPath = "audio/" + itemId + ".silk";
                var destinationPath = ExportPathSafety.CombineUnderRoot(stagingRoot, relativeAudioPath);
                FileHashMetadata sourceMetadata;
                if (request.LinkMode == DatasetLinkMode.LinkedView)
                {
                    sourceMetadata = await VerifyArtifactAsync(sourcePath, entry.OriginalByteLength, entry.OriginalSha256, cancellationToken).ConfigureAwait(false);
                    if (!TryCreateHardLink(destinationPath, sourcePath))
                    {
                        throw new AppFailureException(ErrorCode.InvalidRequest, "The requested Linked View could not create a hard link.");
                    }

                    usedHardLinks = true;
                }
                else
                {
                    // VerifiedCopy is the normal, independent dataset mode.
                    // Hash the source while copying under one read handle; a
                    // second full read of the newly created output is only
                    // needed by the later independent Verify operation.
                    sourceMetadata = await CopyAndVerifyAsync(
                        sourcePath,
                        destinationPath,
                        entry.OriginalByteLength,
                        entry.OriginalSha256,
                        cancellationToken).ConfigureAwait(false);
                }

                var outputMetadata = sourceMetadata;
                datasetItems.Add(new VoiceDatasetEntry(
                    itemId,
                    relativeAudioPath,
                    outputMetadata.Sha256,
                    outputMetadata.ByteLength,
                    entry.DurationMs,
                    entry.QualityFlags,
                    TrainingEligibility.Eligible,
                    Selected: true));
                buildItems.Add(new DatasetBuildItem(itemId, relativeAudioPath, outputMetadata.Sha256, outputMetadata.ByteLength, entry.DurationMs));
            }

            var datasetManifest = new VoiceDatasetManifest(DateTimeOffset.UtcNow, manifest.RunId, datasetItems);
            var profileOutputPath = Path.Combine(stagingRoot, "selection-profile.json");
            var datasetManifestPath = Path.Combine(stagingRoot, "dataset.json");
            var datasetCsvPath = Path.Combine(stagingRoot, "dataset.csv");
            var buildManifestPath = Path.Combine(stagingRoot, "build-manifest.json");
            await AtomicFileWriter.WriteJsonAsync(profileOutputPath, profile, JsonOptions, cancellationToken).ConfigureAwait(false);
            await AtomicFileWriter.WriteJsonAsync(datasetManifestPath, datasetManifest, JsonOptions, cancellationToken).ConfigureAwait(false);
            await VoiceManifestCsvWriter.WritePortableAsync(datasetCsvPath, datasetManifest, cancellationToken).ConfigureAwait(false);
            var datasetManifestSha256 = await FileHashing.ComputeSha256Async(datasetManifestPath, cancellationToken).ConfigureAwait(false);
            var datasetCsvSha256 = await FileHashing.ComputeSha256Async(datasetCsvPath, cancellationToken).ConfigureAwait(false);
            var profileOutputSha256 = await FileHashing.ComputeSha256Async(profileOutputPath, cancellationToken).ConfigureAwait(false);
            await AtomicFileWriter.WriteJsonAsync(
                buildManifestPath,
                new DatasetBuildManifest(
                    profile.SelectionFingerprint,
                    manifestSha256,
                    profileSha256,
                    DateTimeOffset.UtcNow,
                    buildItems,
                    datasetManifestSha256,
                    datasetCsvSha256,
                    profileOutputSha256,
                    LinkMode: request.LinkMode,
                    SemanticProfileHash: profile.SemanticProfileHash,
                    BuildFingerprint: buildFingerprint,
                    AudioProfileFingerprint: audioProfile?.ProfileFingerprint,
                    DecoderIdentity: decoderIdentity),
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
            var buildManifestSha256 = await FileHashing.ComputeSha256Async(buildManifestPath, cancellationToken).ConfigureAwait(false);
            await AtomicFileWriter.WriteJsonAsync(
                Path.Combine(stagingRoot, DatasetMetadataDescriptorFileName),
                new DatasetMetadataCommitDescriptor(
                    Guid.NewGuid().ToString("N"),
                    profile.SelectionFingerprint,
                    manifestSha256,
                    profileOutputSha256,
                    datasetManifestSha256,
                    datasetCsvSha256,
                    buildManifestSha256,
                    request.LinkMode,
                    DateTimeOffset.UtcNow),
                JsonOptions,
                cancellationToken).ConfigureAwait(false);

            Directory.Move(stagingRoot, outputRoot);
            if (request.LinkMode == DatasetLinkMode.LinkedView)
            {
                MarkLinkedViewDirectoryReadOnly(outputRoot);
            }
            return new DatasetBuildResult(
                outputRoot,
                profile.SelectionFingerprint,
                manifestPath,
                Path.Combine(outputRoot, "dataset.json"),
                Path.Combine(outputRoot, "dataset.csv"),
                Path.Combine(outputRoot, "build-manifest.json"),
                datasetItems.Count,
                datasetManifest.TotalDurationMs,
                datasetManifest.TotalByteLength,
                usedHardLinks,
                request.LinkMode,
                buildFingerprint,
                audioProfile?.ProfileFingerprint,
                decoderIdentity);
        }
        finally
        {
            TryDeleteDirectory(stagingRoot);
            if (decoder is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Decodes a single curated SILK artifact to a transient WAV for preview.
    /// The WAV is written under the app temp root and is never persisted into
    /// the raw export or a curated dataset. The caller owns its lifetime.
    /// </summary>
    public async Task<DatasetPreviewDecodeResult> PreviewDecodeAsync(
        string exportDirectory,
        string itemId,
        int sampleRate,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        var exportRoot = Path.GetFullPath(exportDirectory);
        if (!Directory.Exists(exportRoot))
        {
            throw new AppFailureException(ErrorCode.InvalidRequest, "The export directory does not exist.");
        }

        var manifestPath = ResolveManifestPath(exportRoot, null);
        EnsureUnderRoot(exportRoot, manifestPath);
        if (!File.Exists(manifestPath))
        {
            throw new AppFailureException(ErrorCode.InvalidRequest, "The export manifest does not exist.");
        }

        var manifest = await ReadAsync<VoiceExportManifest>(manifestPath, cancellationToken).ConfigureAwait(false);
        if (!manifest.Entries.Any())
        {
            throw new AppFailureException(ErrorCode.InvalidRequest, "The export manifest is empty.");
        }

        VoiceExportEntry? match = null;
        foreach (var entry in manifest.Entries)
        {
            if (string.Equals(ExportItemIdentity.ComputeItemId(entry, manifest.DatasetNamespaceKey), itemId, StringComparison.OrdinalIgnoreCase))
            {
                match = entry;
                break;
            }
        }

        if (match is null || !VoiceExportEntryValidation.HasValidOriginalArtifact(match))
        {
            throw new AppFailureException(ErrorCode.InvalidRequest, "The item is not a valid original artifact in the export manifest.");
        }

        var decoder = _decoderFactory?.Create(sampleRate)
            ?? throw new AppFailureException(
                ErrorCode.DurationResolverUnavailable,
                "No usable SILK decoder is configured; configure a reviewed decoder before previewing audio.");
        string? decoderIdentity = decoder is IVoiceDecoderIdentity identity ? identity.DecoderIdentity : null;
        var directory = Path.Combine(Path.GetTempPath(), "wechatvoice-preview");
        Directory.CreateDirectory(directory);
        var destinationPath = Path.Combine(directory, itemId + "-" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            var sourcePath = ResolveArtifactPath(exportRoot, match.OriginalPath);
            var decoded = await DecodeToWavAsync(decoder, sourcePath, destinationPath, match, new AudioBuildProfile(sampleRate), _ffmpegNormalizer, cancellationToken).ConfigureAwait(false);
            var metadata = new FileInfo(destinationPath);
            return new DatasetPreviewDecodeResult(itemId, destinationPath, metadata.Length, decoded.DurationMs, decoderIdentity);
        }
        catch
        {
            TryDeleteFile(destinationPath);
            throw;
        }
        finally
        {
            if (decoder is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public async Task<DatasetDeletePreview> PreviewDeleteAsync(
        DatasetDeleteRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var exportRoot = Path.GetFullPath(request.ExportDirectory);
        var outputRoot = Path.GetFullPath(request.OutputDirectory);
        EnsureDatasetOutputUnderRoot(exportRoot, outputRoot);
        if (!Directory.Exists(exportRoot) || !Directory.Exists(outputRoot))
        {
            throw new AppFailureException(ErrorCode.InvalidRequest, "The curated dataset directory does not exist.");
        }

        await using var rootLock = await ExportRootLock.AcquireForOperationAsync(
            exportRoot,
            ExportRootLockMode.Exclusive,
            Guid.NewGuid().ToString("N"),
            runId: null,
            cancellationToken).ConfigureAwait(false);
        await RecoverDatasetMetadataTransactionsUnderLockAsync(exportRoot, cancellationToken).ConfigureAwait(false);
        return await ValidateDeleteAsync(request, exportRoot, outputRoot, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DatasetDeleteResult> DeleteAsync(
        DatasetDeleteRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var exportRoot = Path.GetFullPath(request.ExportDirectory);
        var outputRoot = Path.GetFullPath(request.OutputDirectory);
        EnsureDatasetOutputUnderRoot(exportRoot, outputRoot);
        if (!Directory.Exists(exportRoot) || !Directory.Exists(outputRoot))
        {
            throw new AppFailureException(ErrorCode.InvalidRequest, "The curated dataset directory does not exist.");
        }

        await using var rootLock = await ExportRootLock.AcquireForOperationAsync(
            exportRoot,
            ExportRootLockMode.Exclusive,
            Guid.NewGuid().ToString("N"),
            runId: null,
            cancellationToken).ConfigureAwait(false);
        await RecoverDatasetMetadataTransactionsUnderLockAsync(exportRoot, cancellationToken).ConfigureAwait(false);
        var preview = await ValidateDeleteAsync(request, exportRoot, outputRoot, cancellationToken).ConfigureAwait(false);
        if (!request.Confirmed)
        {
            throw new AppFailureException(ErrorCode.InvalidRequest, "A second explicit confirmation is required before deleting the derived dataset.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        ClearReadOnlyAttributes(outputRoot);
        Directory.Delete(outputRoot, recursive: true);
        if (Directory.Exists(outputRoot))
        {
            throw new IOException("The curated dataset directory could not be deleted completely.");
        }

        return new DatasetDeleteResult(
            preview.OutputDirectory,
            preview.SelectionFingerprint,
            preview.ItemCount,
            preview.TotalByteLength);
    }

    private static async Task<DatasetDeletePreview> ValidateDeleteAsync(
        DatasetDeleteRequest request,
        string exportRoot,
        string outputRoot,
        CancellationToken cancellationToken)
    {
        if ((File.GetAttributes(outputRoot) & FileAttributes.ReparsePoint) != 0)
        {
            throw new AppFailureException(ErrorCode.InvalidRequest, "The curated dataset directory cannot be a reparse point.");
        }
        EnsureNoReparsePoints(outputRoot);

        var outputProfilePath = Path.Combine(outputRoot, "selection-profile.json");
        if (!File.Exists(outputProfilePath))
        {
            throw new AppFailureException(ErrorCode.InvalidRequest, "The curated dataset selection profile is missing.");
        }

        // Deleting a previously built dataset must not depend on whichever
        // selection profile happens to be current at the export root now.
        // The output's own profile and build manifest are the identity being
        // deleted; the source private manifest is still required and is
        // located by its bound run/hash.
        var outputProfile = await ReadAsync<DatasetSelectionProfile>(outputProfilePath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(outputProfile.SelectionFingerprint, request.SelectionFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            throw new AppFailureException(ErrorCode.InvalidRequest, "The requested dataset selection fingerprint does not match the dataset profile.");
        }

        var sourceManifestPath = await ResolveBoundSourceManifestAsync(exportRoot, outputProfile, cancellationToken).ConfigureAwait(false);
        var sourceManifestSha256 = await FileHashing.ComputeSha256Async(sourceManifestPath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(sourceManifestSha256, outputProfile.ManifestSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new AppFailureException(ErrorCode.InvalidRequest, "The dataset source manifest no longer matches its recorded binding.");
        }

        var verified = await TryVerifyExistingAsync(
            outputRoot,
            outputProfile,
            sourceManifestPath,
            outputProfile.ManifestSha256,
            await FileHashing.ComputeSha256Async(outputProfilePath, cancellationToken).ConfigureAwait(false),
            linkMode: null,
            expectedBuildFingerprint: null,
            expectedAudioProfileFingerprint: null,
            cancellationToken).ConfigureAwait(false);
        if (verified is null
            || !string.Equals(verified.SelectionFingerprint, request.SelectionFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            throw new AppFailureException(ErrorCode.InvalidRequest, "The curated dataset build manifest or audio files failed verification.");
        }

        return new DatasetDeletePreview(
            outputRoot,
            verified.SelectionFingerprint,
            verified.ItemCount,
            verified.TotalByteLength);
    }

    private static async Task<string> ResolveBoundSourceManifestAsync(
        string exportRoot,
        DatasetSelectionProfile profile,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(profile.RunId)
            || profile.RunId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new AppFailureException(ErrorCode.InvalidRequest, "The dataset profile contains an invalid source run identity.");
        }

        var candidates = new[]
        {
            Path.Combine(exportRoot, "runs", ExportManifestLayout.RunPrivateManifestFileName(profile.RunId)),
            Path.Combine(exportRoot, ExportManifestLayout.PrivateManifestFileName),
        };
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(candidate)
                || (File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            if (string.Equals(
                    await FileHashing.ComputeSha256Async(candidate, cancellationToken).ConfigureAwait(false),
                    profile.ManifestSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        throw new AppFailureException(ErrorCode.InvalidRequest, "The dataset's bound private export manifest is missing or changed.");
    }

    public async Task<DatasetBuildVerificationResult> VerifyAsync(
        DatasetBuildRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var exportRoot = Path.GetFullPath(request.ExportDirectory);
        var outputRoot = Path.GetFullPath(request.OutputDirectory ?? Path.Combine(
            exportRoot,
            "datasets",
            "unknown-selection"));
        if (!Directory.Exists(exportRoot))
        {
            return InvalidVerification(outputRoot, "export-missing", null, "The export directory does not exist.");
        }

        await using var rootLock = await ExportRootLock.AcquireForOperationAsync(
            exportRoot,
            ExportRootLockMode.Exclusive,
            Guid.NewGuid().ToString("N"),
            runId: null,
            cancellationToken).ConfigureAwait(false);
        try
        {
            var exportStore = new FileSystemVoiceExportStore(exportRoot);
            await exportStore.RecoverPendingTransactionsUnderLockAsync(cancellationToken, rootLock).ConfigureAwait(false);
            await RecoverDatasetMetadataTransactionsUnderLockAsync(exportRoot, cancellationToken).ConfigureAwait(false);
            var inputs = await LoadInputsAsync(
                exportRoot,
                request.ManifestPath,
                request.ProfilePath,
                cancellationToken).ConfigureAwait(false);
            var buildFingerprint = ComputeBuildFingerprint(inputs.Profile, request.AudioProfile);
            outputRoot = Path.GetFullPath(request.OutputDirectory ?? Path.Combine(
                exportRoot,
                "datasets",
                buildFingerprint));
            EnsureUnderRoot(exportRoot, outputRoot);
            // When the caller does not supply an audio profile, the existing
            // build manifest is the authoritative record of the build identity
            // (a WAV build carries a combined selection+audio fingerprint).
            // Derive the expected fingerprints from it so a valid WAV build is
            // verified correctly without re-passing the profile.
            var buildManifestPath = Path.Combine(outputRoot, "build-manifest.json");
            DatasetBuildManifest? existingBuild = null;
            if (File.Exists(buildManifestPath))
            {
                existingBuild = await ReadAsync<DatasetBuildManifest>(buildManifestPath, cancellationToken).ConfigureAwait(false);
            }

            var expectedBuildFingerprint = existingBuild?.BuildFingerprint ?? buildFingerprint;
            var expectedAudioProfileFingerprint = request.AudioProfile?.ProfileFingerprint
                ?? existingBuild?.AudioProfileFingerprint;
            var existing = await TryVerifyExistingAsync(
                outputRoot,
                inputs.Profile,
                inputs.ManifestPath,
                inputs.ManifestSha256,
                inputs.ProfileSha256,
                linkMode: null,
                expectedBuildFingerprint: expectedBuildFingerprint,
                expectedAudioProfileFingerprint: expectedAudioProfileFingerprint,
                cancellationToken).ConfigureAwait(false);
            return existing is null
                ? InvalidVerification(outputRoot, "dataset-invalid", null, "The curated dataset is missing or failed verification.")
                : ToVerification(existing);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException or AppFailureException)
        {
            return InvalidVerification(outputRoot, "dataset-invalid", null, "The curated dataset inputs or metadata are invalid.");
        }
    }

    /// <summary>
    /// Rebuilds only the derived dataset metadata. Audio files are verified in
    /// place and are never copied, replaced, or deleted by this operation.
    /// </summary>
    public async Task<DatasetBuildVerificationResult> RepairAsync(
        DatasetBuildRepairRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var exportRoot = Path.GetFullPath(request.ExportDirectory);
        var outputRoot = Path.GetFullPath(request.OutputDirectory);
        EnsureUnderRoot(exportRoot, outputRoot);
        if (!Directory.Exists(exportRoot) || !Directory.Exists(outputRoot))
        {
            return InvalidVerification(outputRoot, "dataset-missing", null, "The curated dataset directory does not exist.");
        }

        string? existingBuildFingerprint = null;
        string? existingAudioProfileFingerprint = null;
        await using (var rootLock = await ExportRootLock.AcquireForOperationAsync(
            exportRoot,
            ExportRootLockMode.Exclusive,
            Guid.NewGuid().ToString("N"),
            runId: null,
            cancellationToken).ConfigureAwait(false))
        {
            if ((File.GetAttributes(outputRoot) & FileAttributes.ReadOnly) != 0)
            {
                // Linked Views are intentionally read-only after a successful
                // build. Recovery must be able to replace only derived
                // metadata, never the audio files, before verification.
                ClearReadOnlyAttributes(outputRoot);
            }
            await RecoverDatasetMetadataTransactionsUnderLockAsync(exportRoot, cancellationToken).ConfigureAwait(false);
            if ((File.GetAttributes(outputRoot) & FileAttributes.ReparsePoint) != 0)
            {
                return InvalidVerification(outputRoot, "dataset-reparse-point", null, "The curated dataset directory cannot be a reparse point.");
            }
            try
            {
                EnsureNoReparsePoints(outputRoot);
            }
            catch (AppFailureException exception)
            {
                return InvalidVerification(outputRoot, "dataset-reparse-point", null, exception.Message);
            }

            var inputs = await LoadInputsAsync(
                exportRoot,
                request.ManifestPath,
                request.ProfilePath,
                cancellationToken).ConfigureAwait(false);
            var existingBuildPath = Path.Combine(outputRoot, "build-manifest.json");
            DatasetBuildManifest? existingBuild = null;
            if (File.Exists(existingBuildPath))
            {
                existingBuild = await ReadAsync<DatasetBuildManifest>(existingBuildPath, cancellationToken).ConfigureAwait(false);
                existingBuildFingerprint = existingBuild.BuildFingerprint;
                existingAudioProfileFingerprint = existingBuild.AudioProfileFingerprint;
            }
            if (existingBuild?.LinkMode == DatasetLinkMode.LinkedView)
            {
                ClearReadOnlyAttributes(outputRoot);
            }
            var repairItems = CreateRepairAudioItems(inputs, existingBuild);
            var expectedAudioPaths = repairItems
                .Select(static item => item.RelativeAudioPath.Replace('/', Path.DirectorySeparatorChar))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (HasUnexpectedAudioFiles(outputRoot, expectedAudioPaths))
            {
                if (existingBuild?.LinkMode == DatasetLinkMode.LinkedView)
                {
                    MarkLinkedViewDirectoryReadOnly(outputRoot);
                }

                return InvalidVerification(outputRoot, "dataset-audio-set-invalid", null, "The curated dataset contains missing, extra, or unsafe audio files.");
            }

            var datasetItems = new List<VoiceDatasetEntry>(repairItems.Count);
            foreach (var repairItem in repairItems)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = ExportPathSafety.CombineUnderRoot(outputRoot, repairItem.RelativeAudioPath);
                var metadata = await VerifyArtifactAsync(
                    path,
                    repairItem.ExpectedByteLength,
                    repairItem.ExpectedSha256,
                    cancellationToken).ConfigureAwait(false);
                datasetItems.Add(new VoiceDatasetEntry(
                    repairItem.ItemId,
                    repairItem.RelativeAudioPath,
                    metadata.Sha256,
                    metadata.ByteLength,
                    repairItem.DurationMs,
                    existingBuild?.AudioProfileFingerprint is not null
                        ? MergeQualityFlags(
                            inputs.Entries[repairItem.ItemId].QualityFlags,
                            ComputeWavQualityFlags(path, repairItem.DurationMs, cancellationToken))
                        : inputs.Entries[repairItem.ItemId].QualityFlags,
                    TrainingEligibility.Eligible,
                    Selected: true));
            }

            var transactionId = Guid.NewGuid().ToString("N");
            var stagingRoot = Path.Combine(outputRoot, ".dataset-metadata.staging-" + transactionId);
            var transactionPath = Path.Combine(outputRoot, DatasetMetadataTransactionFileName);
            var transactionPersisted = false;
            DatasetMetadataTransactionDocument? transaction = null;
            try
            {
                Directory.CreateDirectory(stagingRoot);
                var datasetManifest = new VoiceDatasetManifest(
                    DateTimeOffset.UtcNow,
                    inputs.Manifest.RunId,
                    datasetItems);
                var profileOutputPath = Path.Combine(stagingRoot, "selection-profile.json");
                var datasetManifestPath = Path.Combine(stagingRoot, "dataset.json");
                var datasetCsvPath = Path.Combine(stagingRoot, "dataset.csv");
                var buildManifestPath = Path.Combine(stagingRoot, "build-manifest.json");
                await AtomicFileWriter.WriteJsonAsync(profileOutputPath, inputs.Profile, JsonOptions, cancellationToken).ConfigureAwait(false);
                await AtomicFileWriter.WriteJsonAsync(datasetManifestPath, datasetManifest, JsonOptions, cancellationToken).ConfigureAwait(false);
                await VoiceManifestCsvWriter.WritePortableAsync(datasetCsvPath, datasetManifest, cancellationToken).ConfigureAwait(false);
                var datasetManifestSha256 = await FileHashing.ComputeSha256Async(datasetManifestPath, cancellationToken).ConfigureAwait(false);
                var datasetCsvSha256 = await FileHashing.ComputeSha256Async(datasetCsvPath, cancellationToken).ConfigureAwait(false);
                var profileOutputSha256 = await FileHashing.ComputeSha256Async(profileOutputPath, cancellationToken).ConfigureAwait(false);
                var buildItems = repairItems
                    .Zip(datasetItems, static (repairItem, datasetItem) => new DatasetBuildItem(
                        repairItem.ItemId,
                        repairItem.RelativeAudioPath,
                        datasetItem.Sha256,
                        datasetItem.ByteLength,
                        datasetItem.DurationMs))
                    .ToArray();
                await AtomicFileWriter.WriteJsonAsync(
                    buildManifestPath,
                    new DatasetBuildManifest(
                        inputs.Profile.SelectionFingerprint,
                        inputs.ManifestSha256,
                        inputs.ProfileSha256,
                        DateTimeOffset.UtcNow,
                        buildItems,
                        datasetManifestSha256,
                        datasetCsvSha256,
                        profileOutputSha256,
                        LinkMode: existingBuild?.LinkMode ?? DatasetLinkMode.VerifiedCopy,
                        SemanticProfileHash: inputs.Profile.SemanticProfileHash,
                        BuildFingerprint: existingBuild?.BuildFingerprint,
                        AudioProfileFingerprint: existingBuild?.AudioProfileFingerprint,
                        DecoderIdentity: existingBuild?.DecoderIdentity),
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);
                var buildManifestSha256 = await FileHashing.ComputeSha256Async(buildManifestPath, cancellationToken).ConfigureAwait(false);
                var descriptor = new DatasetMetadataCommitDescriptor(
                    transactionId,
                    inputs.Profile.SelectionFingerprint,
                    inputs.ManifestSha256,
                    profileOutputSha256,
                    datasetManifestSha256,
                    datasetCsvSha256,
                    buildManifestSha256,
                    existingBuild?.LinkMode ?? DatasetLinkMode.VerifiedCopy,
                    DateTimeOffset.UtcNow);
                var stagedDescriptorPath = Path.Combine(stagingRoot, DatasetMetadataDescriptorFileName);
                await AtomicFileWriter.WriteJsonAsync(stagedDescriptorPath, descriptor, JsonOptions, cancellationToken).ConfigureAwait(false);
                var descriptorSha256 = await FileHashing.ComputeSha256Async(stagedDescriptorPath, cancellationToken).ConfigureAwait(false);
                transaction = new DatasetMetadataTransactionDocument(
                    transactionId,
                    Path.GetFileName(stagingRoot),
                    DatasetMetadataTransactionState.Prepared,
                    DateTimeOffset.UtcNow,
                    descriptor,
                    descriptorSha256);
                await AtomicFileWriter.WriteJsonAsync(transactionPath, transaction, JsonOptions, cancellationToken).ConfigureAwait(false);
                transactionPersisted = true;
                transaction = transaction with
                {
                    State = DatasetMetadataTransactionState.Publishing,
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                };
                await AtomicFileWriter.WriteJsonAsync(transactionPath, transaction, JsonOptions, cancellationToken).ConfigureAwait(false);
                await PublishDatasetMetadataAsync(outputRoot, stagingRoot, transaction.Descriptor, transaction.DescriptorSha256, cancellationToken).ConfigureAwait(false);
                transaction = transaction with
                {
                    State = DatasetMetadataTransactionState.Completed,
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                };
                await AtomicFileWriter.WriteJsonAsync(transactionPath, transaction, JsonOptions, cancellationToken).ConfigureAwait(false);
                TryDeleteDirectory(stagingRoot);
                TryDeleteFile(transactionPath);
                if (existingBuild?.LinkMode == DatasetLinkMode.LinkedView)
                {
                    MarkLinkedViewDirectoryReadOnly(outputRoot);
                }
            }
            catch
            {
                if (transactionPersisted && transaction is not null)
                {
                    try
                    {
                        await AtomicFileWriter.WriteJsonAsync(
                            transactionPath,
                            transaction with
                            {
                                State = DatasetMetadataTransactionState.FailedRecoverable,
                                UpdatedAtUtc = DateTimeOffset.UtcNow,
                            },
                            JsonOptions,
                            CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Preserve the staging directory for the next locked
                        // recovery attempt even if this marker write failed.
                    }
                }
                else
                {
                    TryDeleteDirectory(stagingRoot);
                }

                throw;
            }

            var verified = await TryVerifyExistingAsync(
                outputRoot,
                inputs.Profile,
                inputs.ManifestPath,
                inputs.ManifestSha256,
                inputs.ProfileSha256,
                linkMode: null,
                expectedBuildFingerprint: existingBuildFingerprint,
                expectedAudioProfileFingerprint: existingAudioProfileFingerprint,
                cancellationToken).ConfigureAwait(false);
            return verified is null
                ? InvalidVerification(outputRoot, "dataset-invalid", null, "The curated dataset is missing or failed verification after repair.")
                : ToVerification(verified);
        }
    }

    public async Task RecoverPendingMetadataTransactionsAsync(
        string exportRoot,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(exportRoot))
        {
            return;
        }

        await using var rootLock = await ExportRootLock.AcquireForOperationAsync(
            exportRoot,
            ExportRootLockMode.Exclusive,
            Guid.NewGuid().ToString("N"),
            runId: null,
            cancellationToken).ConfigureAwait(false);
        await RecoverDatasetMetadataTransactionsUnderLockAsync(exportRoot, cancellationToken).ConfigureAwait(false);
    }

    private static async Task RecoverDatasetMetadataTransactionsUnderLockAsync(
        string exportRoot,
        CancellationToken cancellationToken)
    {
        var datasetsRoot = Path.Combine(exportRoot, "datasets");
        if (!Directory.Exists(datasetsRoot))
        {
            return;
        }

        if ((File.GetAttributes(datasetsRoot) & FileAttributes.ReparsePoint) != 0)
        {
            throw new AppFailureException(ErrorCode.InvalidRequest, "The dataset root cannot be a reparse point.");
        }

        foreach (var outputRoot in Directory.EnumerateDirectories(datasetsRoot, "*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((File.GetAttributes(outputRoot) & FileAttributes.ReparsePoint) != 0)
            {
                throw new AppFailureException(ErrorCode.InvalidRequest, "A dataset output directory cannot be a reparse point.");
            }

            var transactionPath = Path.Combine(outputRoot, DatasetMetadataTransactionFileName);
            if (File.Exists(transactionPath))
            {
                if ((File.GetAttributes(transactionPath) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new AppFailureException(ErrorCode.InvalidRequest, "The dataset metadata transaction cannot be a reparse point.");
                }

                EnsureNoReparsePoints(outputRoot);
                await RecoverDatasetMetadataTransactionAsync(outputRoot, transactionPath, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task RecoverDatasetMetadataTransactionAsync(
        string outputRoot,
        string transactionPath,
        CancellationToken cancellationToken)
    {
        var transaction = await ReadAsync<DatasetMetadataTransactionDocument>(transactionPath, cancellationToken).ConfigureAwait(false);
        if (transaction.Descriptor is null
            || string.IsNullOrWhiteSpace(transaction.TransactionId)
            || !string.Equals(transaction.TransactionId, transaction.Descriptor.TransactionId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(transaction.StagingDirectoryName)
            || Path.IsPathRooted(transaction.StagingDirectoryName)
            || !string.Equals(transaction.StagingDirectoryName, Path.GetFileName(transaction.StagingDirectoryName), StringComparison.Ordinal)
            || transaction.StagingDirectoryName.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The dataset metadata transaction contains an unsafe staging path.");
        }

        var stagingRoot = Path.Combine(outputRoot, transaction.StagingDirectoryName);
        if (Directory.Exists(stagingRoot)
            && (File.GetAttributes(stagingRoot) & FileAttributes.ReparsePoint) != 0)
        {
            throw new AppFailureException(ErrorCode.InvalidRequest, "The dataset metadata staging directory cannot be a reparse point.");
        }

        ValidateSha256(transaction.DescriptorSha256, "dataset metadata descriptor");
        ValidateSha256(transaction.Descriptor.SourceManifestSha256, "dataset source manifest");
        ValidateSha256(transaction.Descriptor.SelectionProfileSha256, "dataset selection profile");
        ValidateSha256(transaction.Descriptor.DatasetManifestSha256, "dataset manifest");
        ValidateSha256(transaction.Descriptor.DatasetCsvSha256, "dataset CSV");
        ValidateSha256(transaction.Descriptor.BuildManifestSha256, "dataset build manifest");

        await PublishDatasetMetadataAsync(
            outputRoot,
            stagingRoot,
            transaction.Descriptor,
            transaction.DescriptorSha256,
            cancellationToken).ConfigureAwait(false);

        var completed = transaction with
        {
            State = DatasetMetadataTransactionState.Completed,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        await AtomicFileWriter.WriteJsonAsync(transactionPath, completed, JsonOptions, cancellationToken).ConfigureAwait(false);
        TryDeleteDirectory(stagingRoot);
        TryDeleteFile(transactionPath);
    }

    private static async Task PublishDatasetMetadataAsync(
        string outputRoot,
        string stagingRoot,
        DatasetMetadataCommitDescriptor descriptor,
        string descriptorSha256,
        CancellationToken cancellationToken)
    {
        foreach (var fileName in DatasetMetadataFileNames)
        {
            await PublishDatasetMetadataFileAsync(
                stagingRoot,
                outputRoot,
                fileName,
                GetDatasetMetadataHash(descriptor, fileName),
                cancellationToken).ConfigureAwait(false);
        }

        await PublishDatasetMetadataFileAsync(
            stagingRoot,
            outputRoot,
            DatasetMetadataDescriptorFileName,
            descriptorSha256,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task PublishDatasetMetadataFileAsync(
        string stagingRoot,
        string outputRoot,
        string fileName,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        var stagedPath = ExportPathSafety.CombineUnderRoot(stagingRoot, fileName);
        var destinationPath = ExportPathSafety.CombineUnderRoot(outputRoot, fileName);
        if (File.Exists(stagedPath)
            && (File.GetAttributes(stagedPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new AppFailureException(ErrorCode.InvalidRequest, "A staged dataset metadata file cannot be a reparse point.");
        }
        if (File.Exists(destinationPath)
            && (File.GetAttributes(destinationPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new AppFailureException(ErrorCode.InvalidRequest, "A dataset metadata file cannot be a reparse point.");
        }

        if (File.Exists(destinationPath)
            && string.Equals(
                await FileHashing.ComputeSha256Async(destinationPath, cancellationToken).ConfigureAwait(false),
                expectedSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!File.Exists(stagedPath))
        {
            throw new InvalidDataException("A dataset metadata transaction is missing a staged file required for recovery.");
        }

        var stagedHash = await FileHashing.ComputeSha256Async(stagedPath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(stagedHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A staged dataset metadata file failed its durable hash binding.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        if (!File.Exists(destinationPath))
        {
            File.Move(stagedPath, destinationPath);
        }
        else
        {
            var backupPath = destinationPath + ".backup-" + Guid.NewGuid().ToString("N");
            try
            {
                File.Replace(stagedPath, destinationPath, backupPath, ignoreMetadataErrors: true);
            }
            finally
            {
                TryDeleteFile(backupPath);
            }
        }
    }

    private static string GetDatasetMetadataHash(
        DatasetMetadataCommitDescriptor descriptor,
        string fileName)
        => fileName switch
        {
            "selection-profile.json" => descriptor.SelectionProfileSha256,
            "dataset.json" => descriptor.DatasetManifestSha256,
            "dataset.csv" => descriptor.DatasetCsvSha256,
            "build-manifest.json" => descriptor.BuildManifestSha256,
            "dataset-metadata-commit.json" => throw new InvalidOperationException("The metadata descriptor hash is stored separately."),
            _ => throw new InvalidDataException("The dataset metadata transaction contains an unknown file.")
        };

    private static void ValidateSha256(string? value, string description)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 64 || !value.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException($"The {description} binding is not a SHA-256 value.");
        }
    }

    private static async Task<BuildInputs> LoadInputsAsync(
        string exportRoot,
        string? requestedManifestPath,
        string? requestedProfilePath,
        CancellationToken cancellationToken)
    {
        var manifestPath = ResolveManifestPath(exportRoot, requestedManifestPath);
        var profilePath = Path.GetFullPath(requestedProfilePath ?? DatasetSelectionProfileStore.GetPath(exportRoot));
        EnsureUnderRoot(exportRoot, manifestPath);
        EnsureUnderRoot(exportRoot, profilePath);
        if (!File.Exists(manifestPath) || !File.Exists(profilePath))
        {
            throw new AppFailureException(ErrorCode.InvalidRequest, "A committed private export manifest and selection profile are required.");
        }

        var manifestSha256 = await FileHashing.ComputeSha256Async(manifestPath, cancellationToken).ConfigureAwait(false);
        var profileSha256 = await FileHashing.ComputeSha256Async(profilePath, cancellationToken).ConfigureAwait(false);
        var manifest = await ReadAsync<VoiceExportManifest>(manifestPath, cancellationToken).ConfigureAwait(false);
        var profile = await ReadAsync<DatasetSelectionProfile>(profilePath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(profile.ManifestSha256, manifestSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new AppFailureException(ErrorCode.InvalidRequest, "The selection profile is not bound to the current private export manifest.");
        }

        if (!string.Equals(profile.RunId, manifest.RunId, StringComparison.Ordinal))
        {
            throw new AppFailureException(ErrorCode.InvalidRequest, "The selection profile RunId is not bound to the private export manifest.");
        }

        if (profile.DuplicateRepresentativeItemIds.Any(itemId => !profile.SelectedItemIds.Contains(itemId, StringComparer.OrdinalIgnoreCase)))
        {
            throw new AppFailureException(ErrorCode.InvalidRequest, "The selection profile contains a duplicate representative that is not selected.");
        }

        EnsureDatasetNamespace(manifest);
        var entries = manifest.Entries.ToDictionary(
            entry => ExportItemIdentity.ComputeItemId(entry, manifest.DatasetNamespaceKey),
            StringComparer.OrdinalIgnoreCase);
        if (profile.SelectedItemIds.Any(itemId => !entries.ContainsKey(itemId)))
        {
            throw new AppFailureException(ErrorCode.InvalidRequest, "The selection profile references an item outside the export manifest.");
        }

        if (manifest.Entries.Any(static entry => !VoiceExportEntryValidation.HasValidOriginalArtifact(entry)))
        {
            throw new AppFailureException(ErrorCode.InvalidRequest, "The private export manifest contains an item without a valid stable artifact identity.");
        }

        return new BuildInputs(exportRoot, manifestPath, profilePath, manifestSha256, profileSha256, manifest, profile, entries);
    }

    private static IReadOnlyList<SelectedBuildItem> CreateSelectedItems(BuildInputs inputs)
    {
        var selected = new List<SelectedBuildItem>(inputs.Profile.SelectedItemIds.Count);
        foreach (var itemId in inputs.Profile.SelectedItemIds)
        {
            var entry = inputs.Entries[itemId];
            if (!IsTrainingEligible(entry, inputs.Profile.Filters))
            {
                throw new AppFailureException(ErrorCode.InvalidRequest, "The selection profile contains an item that is not explicitly training-eligible and selected.");
            }

            selected.Add(new SelectedBuildItem(
                itemId,
                "audio/" + itemId + ".silk",
                entry));
        }

        return selected;
    }

    private static IReadOnlyList<RepairAudioItem> CreateRepairAudioItems(
        BuildInputs inputs,
        DatasetBuildManifest? existingBuild)
    {
        // A WAV build records the derived audio hashes in the build manifest;
        // the source SILK entry hash no longer matches the on-disk WAV, so
        // repair must verify against the recorded derived identity instead of
        // the source SILK entry.
        if (existingBuild?.AudioProfileFingerprint is not null)
        {
            if (existingBuild.Items is null || existingBuild.Items.Count == 0)
            {
                throw new AppFailureException(ErrorCode.InvalidRequest, "The WAV dataset has no build manifest items to repair against.");
            }

            var selectedIds = inputs.Profile.SelectedItemIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var items = new List<RepairAudioItem>(existingBuild.Items.Count);
            foreach (var item in existingBuild.Items)
            {
                if (!selectedIds.Contains(item.ItemId))
                {
                    throw new AppFailureException(ErrorCode.InvalidRequest, "The WAV build manifest references an item outside the selection.");
                }

                if (string.IsNullOrWhiteSpace(item.RelativeAudioPath)
                    || !item.RelativeAudioPath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                {
                    throw new AppFailureException(ErrorCode.InvalidRequest, "The WAV build manifest contains an unsafe audio path.");
                }

                items.Add(new RepairAudioItem(
                    item.ItemId,
                    item.RelativeAudioPath,
                    item.Sha256,
                    item.ByteLength,
                    item.DurationMs));
            }

            return items;
        }

        return CreateSelectedItems(inputs)
            .Select(static selected => new RepairAudioItem(
                selected.ItemId,
                selected.RelativeAudioPath,
                selected.Entry.OriginalSha256,
                selected.Entry.OriginalByteLength,
                selected.Entry.DurationMs))
            .ToList();
    }

    private static void EnsureDatasetNamespace(VoiceExportManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.DatasetNamespaceKey))
        {
            throw new AppFailureException(ErrorCode.InvalidRequest, "The export manifest has no dataset namespace key for portable item IDs.");
        }

        try
        {
            if (Convert.FromHexString(manifest.DatasetNamespaceKey).Length < 16)
            {
                throw new FormatException();
            }
        }
        catch (FormatException exception)
        {
            throw new AppFailureException(ErrorCode.InvalidRequest, "The export manifest dataset namespace key is invalid.", exception);
        }
    }

    private static DatasetBuildVerificationResult ToVerification(DatasetBuildResult result)
        => new(
            result.OutputDirectory,
            IsValid: true,
            result.SelectionFingerprint,
            result.ItemCount,
            result.TotalDurationMs,
            result.TotalByteLength,
            Array.Empty<DatasetBuildVerificationIssue>(),
            BuildFingerprint: result.BuildFingerprint,
            AudioProfileFingerprint: result.AudioProfileFingerprint,
            DecoderIdentity: result.DecoderIdentity);

    private static DatasetBuildVerificationResult InvalidVerification(
        string outputRoot,
        string code,
        string? relativePath,
        string detail)
        => new(
            outputRoot,
            IsValid: false,
            SelectionFingerprint: null,
            ItemCount: 0,
            TotalDurationMs: 0,
            TotalByteLength: 0,
            [new DatasetBuildVerificationIssue(code, relativePath, detail)]);

    private static bool HasUnexpectedAudioFiles(
        string outputRoot,
        IReadOnlySet<string> expectedAudioPaths)
    {
        var audioRoot = Path.Combine(outputRoot, "audio");
        if (!Directory.Exists(audioRoot))
        {
            return expectedAudioPaths.Count != 0;
        }

        if ((File.GetAttributes(audioRoot) & FileAttributes.ReparsePoint) != 0)
        {
            return true;
        }

        foreach (var directory in Directory.EnumerateDirectories(audioRoot, "*", SearchOption.AllDirectories))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }
        }

        var actual = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(audioRoot, "*", SearchOption.AllDirectories))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }

            actual.Add(Path.GetRelativePath(outputRoot, file));
        }

        return !actual.SetEquals(expectedAudioPaths)
            || expectedAudioPaths.Any(relative => !File.Exists(Path.Combine(outputRoot, relative)));
    }

    private sealed record BuildInputs(
        string ExportRoot,
        string ManifestPath,
        string ProfilePath,
        string ManifestSha256,
        string ProfileSha256,
        VoiceExportManifest Manifest,
        DatasetSelectionProfile Profile,
        IReadOnlyDictionary<string, VoiceExportEntry> Entries);

    private sealed record SelectedBuildItem(
        string ItemId,
        string RelativeAudioPath,
        VoiceExportEntry Entry);

    private sealed record DecodedWav(
        long? DurationMs,
        IReadOnlyList<string> QualityFlags);

    private sealed record RepairAudioItem(
        string ItemId,
        string RelativeAudioPath,
        string ExpectedSha256,
        long ExpectedByteLength,
        long? DurationMs);

    private static async Task<DatasetBuildResult?> TryVerifyExistingAsync(
        string outputRoot,
        DatasetSelectionProfile profile,
        string manifestPath,
        string manifestSha256,
        string profileSha256,
        DatasetLinkMode? linkMode,
        string? expectedBuildFingerprint,
        string? expectedAudioProfileFingerprint,
        CancellationToken cancellationToken)
    {
        if (Directory.Exists(outputRoot)
            && (File.GetAttributes(outputRoot) & FileAttributes.ReparsePoint) != 0)
        {
            return null;
        }

        if (Directory.Exists(outputRoot))
        {
            try
            {
                EnsureNoReparsePoints(outputRoot);
            }
            catch (AppFailureException)
            {
                return null;
            }
        }

        var buildManifestPath = Path.Combine(outputRoot, "build-manifest.json");
        if (!Directory.Exists(outputRoot) || !File.Exists(buildManifestPath))
        {
            return null;
        }

        var build = await ReadAsync<DatasetBuildManifest>(buildManifestPath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(build.SelectionFingerprint, profile.SelectionFingerprint, StringComparison.Ordinal)
            || !string.Equals(build.SourceManifestSha256, manifestSha256, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(build.SemanticProfileHash, profile.SemanticProfileHash, StringComparison.Ordinal)
            || linkMode is not null && build.LinkMode != linkMode.Value
            || expectedBuildFingerprint is not null
                && !string.Equals(build.BuildFingerprint, expectedBuildFingerprint, StringComparison.Ordinal)
            || expectedAudioProfileFingerprint is not null
                && !string.Equals(build.AudioProfileFingerprint, expectedAudioProfileFingerprint, StringComparison.Ordinal))
        {
            return null;
        }

        var metadataDescriptorPath = Path.Combine(outputRoot, DatasetMetadataDescriptorFileName);
        if (!File.Exists(metadataDescriptorPath))
        {
            return null;
        }

        var descriptor = await ReadAsync<DatasetMetadataCommitDescriptor>(metadataDescriptorPath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(descriptor.SelectionFingerprint, profile.SelectionFingerprint, StringComparison.Ordinal)
            || !string.Equals(descriptor.SourceManifestSha256, manifestSha256, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(descriptor.SelectionProfileSha256, build.ProfileOutputSha256, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(descriptor.DatasetManifestSha256, build.DatasetManifestSha256, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(descriptor.DatasetCsvSha256, build.DatasetCsvSha256, StringComparison.OrdinalIgnoreCase)
            || descriptor.LinkMode != build.LinkMode
            || !string.Equals(
                descriptor.BuildManifestSha256,
                await FileHashing.ComputeSha256Async(buildManifestPath, cancellationToken).ConfigureAwait(false),
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var datasetManifestPath = Path.Combine(outputRoot, "dataset.json");
        var datasetCsvPath = Path.Combine(outputRoot, "dataset.csv");
        var profileOutputPath = Path.Combine(outputRoot, "selection-profile.json");
        if (!File.Exists(datasetManifestPath)
            || !File.Exists(datasetCsvPath)
            || !File.Exists(profileOutputPath)
            || string.IsNullOrWhiteSpace(build.DatasetManifestSha256)
            || string.IsNullOrWhiteSpace(build.DatasetCsvSha256)
            || string.IsNullOrWhiteSpace(build.ProfileOutputSha256)
            )
        {
            return null;
        }

        if (!string.Equals(
                await FileHashing.ComputeSha256Async(datasetManifestPath, cancellationToken).ConfigureAwait(false),
                build.DatasetManifestSha256,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                await FileHashing.ComputeSha256Async(datasetCsvPath, cancellationToken).ConfigureAwait(false),
                build.DatasetCsvSha256,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                await FileHashing.ComputeSha256Async(profileOutputPath, cancellationToken).ConfigureAwait(false),
                build.ProfileOutputSha256,
                StringComparison.OrdinalIgnoreCase)
            )
        {
            return null;
        }

        var outputProfile = await ReadAsync<DatasetSelectionProfile>(profileOutputPath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(outputProfile.SelectionFingerprint, profile.SelectionFingerprint, StringComparison.Ordinal)
            || !string.Equals(outputProfile.SemanticProfileHash, profile.SemanticProfileHash, StringComparison.Ordinal)
            || !outputProfile.SelectedItemIds.SequenceEqual(profile.SelectedItemIds, StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        var outputManifest = await ReadAsync<VoiceDatasetManifest>(datasetManifestPath, cancellationToken).ConfigureAwait(false);
        var expectedIds = profile.SelectedItemIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (outputManifest.Items.Count != build.Items.Count
            || outputManifest.Items.Any(item => !expectedIds.Contains(item.ItemId) || !item.Selected))
        {
            return null;
        }

        var buildItemsById = build.Items.ToDictionary(static item => item.ItemId, StringComparer.OrdinalIgnoreCase);
        if (outputManifest.Items.Any(item =>
                !buildItemsById.TryGetValue(item.ItemId, out var expected)
                || !string.Equals(item.RelativeAudioPath, expected.RelativeAudioPath, StringComparison.Ordinal)
                || !string.Equals(item.Sha256, expected.Sha256, StringComparison.OrdinalIgnoreCase)
                || item.ByteLength != expected.ByteLength
                || item.DurationMs != expected.DurationMs))
        {
            return null;
        }

        long duration = 0;
        long bytes = 0;
        foreach (var item in build.Items)
        {
            var path = ExportPathSafety.CombineUnderRoot(outputRoot, item.RelativeAudioPath);
            var metadata = await VerifyArtifactAsync(path, item.ByteLength, item.Sha256, cancellationToken).ConfigureAwait(false);
            bytes = checked(bytes + metadata.ByteLength);
            if (item.DurationMs is > 0) duration = checked(duration + item.DurationMs.Value);
        }

        var expectedAudioPaths = build.Items
            .Select(static item => item.RelativeAudioPath.Replace('/', Path.DirectorySeparatorChar))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (HasUnexpectedAudioFiles(outputRoot, expectedAudioPaths))
        {
            return null;
        }

        return new DatasetBuildResult(
            outputRoot,
            profile.SelectionFingerprint,
            manifestPath,
            datasetManifestPath,
            datasetCsvPath,
            buildManifestPath,
            build.Items.Count,
            duration,
            bytes,
            UsedHardLinks: build.LinkMode == DatasetLinkMode.LinkedView,
            LinkMode: build.LinkMode,
            build.BuildFingerprint,
            build.AudioProfileFingerprint,
            build.DecoderIdentity);
    }

    private static async Task<T> ReadAsync<T>(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The dataset input document is empty.");
    }

    private static string ComputeBuildFingerprint(DatasetSelectionProfile profile, AudioBuildProfile? audioProfile)
        => audioProfile is null
            ? profile.SelectionFingerprint
            : DatasetBuildFingerprint.Compute(profile.SelectionFingerprint, audioProfile);

    private static async Task<DecodedWav> DecodeToWavAsync(
        IVoiceDecoder decoder,
        string sourcePath,
        string destinationPath,
        VoiceExportEntry entry,
        AudioBuildProfile audioProfile,
        FfmpegWavNormalizer? ffmpegNormalizer,
        CancellationToken cancellationToken)
    {
        await VerifyArtifactAsync(sourcePath, entry.OriginalByteLength, entry.OriginalSha256, cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        await using (var input = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var output = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await decoder.DecodeAsync(input, output, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        if (ffmpegNormalizer is not null)
        {
            await ffmpegNormalizer.NormalizeAsync(destinationPath, audioProfile.SampleRate, audioProfile.Mono, cancellationToken).ConfigureAwait(false);
        }

        if (!File.Exists(destinationPath) || new FileInfo(destinationPath).Length == 0)
        {
            throw new AppFailureException(ErrorCode.InvalidRequest, "The decoder produced no WAV output for a selected item.");
        }

        var durationMs = await WavFileValidator.TryReadDurationMsAsync(destinationPath, cancellationToken).ConfigureAwait(false)
            ?? entry.DurationMs;
        var qualityFlags = ComputeWavQualityFlags(destinationPath, durationMs, cancellationToken);
        return new DecodedWav(durationMs, qualityFlags);
    }

    /// <summary>
    /// Runs the bounded, deterministic quality analysis over a decoded PCM WAV
    /// and returns the derived quality flags. A failed or unreadable WAV still
    /// yields a decode-failed flag rather than throwing, so a single item can
    /// never abort the whole dataset build.
    /// </summary>
    private static IReadOnlyList<string> ComputeWavQualityFlags(
        string wavPath,
        long? expectedDurationMs,
        CancellationToken cancellationToken)
    {
        try
        {
            using var stream = new FileStream(
                wavPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.SequentialScan);
            var analysis = VoiceQualityAnalyzer.Analyze(stream, expectedDurationMs, cancellationToken);
            return analysis.Flags;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return [VoiceQualityAnalysis.DecodeFailedFlag];
        }
    }

    private static IReadOnlyList<string> MergeQualityFlags(
        IReadOnlyList<string> sourceFlags,
        IReadOnlyList<string> derivedFlags)
        => sourceFlags
            .Concat(derivedFlags)
            .Where(static flag => !string.IsNullOrWhiteSpace(flag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static bool IsTrainingEligible(VoiceExportEntry entry, DatasetCurationFilters filters)
    {
        if (entry.ExportState == ExportState.Failed
            || entry.HasDecodeError
            || entry.DurationMs is null
            || !VoiceExportEntryValidation.HasValidOriginalArtifact(entry)
            || !DirectionMatches(entry.Direction, filters.DirectionScope)
            || filters.MinimumDurationMs is { } minimumDuration && entry.DurationMs < minimumDuration
            || filters.MaximumDurationMs is { } maximumDuration && entry.DurationMs > maximumDuration
            || filters.MinimumByteLength is { } minimumBytes && entry.OriginalByteLength < minimumBytes
            || filters.MaximumByteLength is { } maximumBytes && entry.OriginalByteLength > maximumBytes)
        {
            return false;
        }

        var flags = entry.QualityFlags.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return !filters.ExcludedQualityFlags.Any(flags.Contains)
            && entry.TrainingEligibility != TrainingEligibility.Rejected;
    }

    private static bool DirectionMatches(VoiceDirection direction, DatasetDirectionScope? scope)
        => scope switch
        {
            DatasetDirectionScope.Incoming => direction == VoiceDirection.Incoming,
            DatasetDirectionScope.Outgoing => direction == VoiceDirection.Outgoing,
            _ => true,
        };

    private static async Task<FileHashMetadata> VerifyArtifactAsync(
        string path,
        long expectedLength,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new AppFailureException(ErrorCode.InvalidRequest, "A selected export artifact is missing or is a reparse point.");
        }

        var metadata = await FileHashing.ComputeMetadataAsync(path, cancellationToken).ConfigureAwait(false);
        if (metadata.ByteLength != expectedLength || !string.Equals(metadata.Sha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new AppFailureException(ErrorCode.InvalidRequest, "A selected export artifact failed hash verification.");
        }

        return metadata;
    }

    private static async Task<FileHashMetadata> CopyAndVerifyAsync(
        string sourcePath,
        string destinationPath,
        long expectedLength,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(sourcePath)
            || (File.GetAttributes(sourcePath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new AppFailureException(ErrorCode.InvalidRequest, "A selected export artifact is missing or is a reparse point.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long length = 0;
        try
        {
            await using var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var destination = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            while (true)
            {
                var count = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                hasher.AppendData(buffer, 0, count);
                length = checked(length + count);
            }

            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            destination.Flush(flushToDisk: true);
        }
        catch
        {
            TryDeleteFile(destinationPath);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }

        var sha256 = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
        if (length != expectedLength || !string.Equals(sha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            TryDeleteFile(destinationPath);
            throw new AppFailureException(ErrorCode.InvalidRequest, "A source export artifact failed hash verification while building the dataset.");
        }

        return new FileHashMetadata(length, sha256, HasPlainSqliteHeader: false);
    }

    private static bool TryCreateHardLink(string destinationPath, string sourcePath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            return CreateHardLink(destinationPath, sourcePath, IntPtr.Zero);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

    private static string ResolveManifestPath(string exportRoot, string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            return Path.GetFullPath(requested);
        }

        return Path.Combine(exportRoot, ExportManifestLayout.PrivateManifestFileName);
    }

    private static string ResolveArtifactPath(string exportRoot, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new AppFailureException(ErrorCode.InvalidRequest, "A manifest artifact path is absolute.");
        }

        return ExportPathSafety.CombineUnderRoot(exportRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    private static void EnsureUnderRoot(string root, string path)
    {
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new AppFailureException(ErrorCode.InvalidRequest, "The dataset path must remain inside the export directory.");
        }
    }

    private static void EnsureDatasetOutputUnderRoot(string exportRoot, string outputRoot)
    {
        var datasetsRoot = Path.GetFullPath(Path.Combine(exportRoot, "datasets"));
        if (Directory.Exists(datasetsRoot)
            && (File.GetAttributes(datasetsRoot) & FileAttributes.ReparsePoint) != 0)
        {
            throw new AppFailureException(ErrorCode.InvalidRequest, "The export datasets directory cannot be a reparse point.");
        }

        var prefix = datasetsRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!outputRoot.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || string.Equals(outputRoot, datasetsRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new AppFailureException(ErrorCode.InvalidRequest, "The dataset output must be a derived child of exportRoot/datasets.");
        }
    }

    private static void MarkLinkedViewDirectoryReadOnly(string outputRoot)
    {
        if (!Directory.Exists(outputRoot)
            || (File.GetAttributes(outputRoot) & FileAttributes.ReparsePoint) != 0)
        {
            throw new AppFailureException(ErrorCode.InvalidRequest, "The Linked View output cannot be a reparse point.");
        }

        File.SetAttributes(outputRoot, File.GetAttributes(outputRoot) | FileAttributes.ReadOnly);
    }

    private static void ClearReadOnlyAttributes(string outputRoot)
    {
        if (!Directory.Exists(outputRoot))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(outputRoot, "*", SearchOption.AllDirectories))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
            {
                File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
            }
        }

        foreach (var path in Directory.EnumerateDirectories(outputRoot, "*", SearchOption.AllDirectories))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
            {
                File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
            }
        }

        File.SetAttributes(outputRoot, File.GetAttributes(outputRoot) & ~FileAttributes.ReadOnly);
    }

    private static void EnsureNoReparsePoints(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var directory in Directory.EnumerateDirectories(current, "*", SearchOption.TopDirectoryOnly))
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new AppFailureException(ErrorCode.InvalidRequest, "The curated dataset contains a reparse point.");
                }

                pending.Push(directory);
            }

            foreach (var file in Directory.EnumerateFiles(current, "*", SearchOption.TopDirectoryOnly))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new AppFailureException(ErrorCode.InvalidRequest, "The curated dataset contains a reparse point.");
                }
            }
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void CleanupStagingDirectories(string parent, string outputName)
    {
        var prefix = "." + outputName + ".staging-";
        foreach (var directory in Directory.EnumerateDirectories(parent, prefix + "*", SearchOption.TopDirectoryOnly))
        {
            try
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new AppFailureException(ErrorCode.InvalidRequest, "A stale dataset staging directory is a reparse point.");
                }

                Directory.Delete(directory, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }
}
