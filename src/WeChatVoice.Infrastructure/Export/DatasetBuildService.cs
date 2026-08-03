using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using WeChatVoice.Core.Errors;
using WeChatVoice.Core.Models;
using WeChatVoice.Infrastructure.Serialization;

namespace WeChatVoice.Infrastructure.Export;

/// <summary>
/// Builds a derived, portable training dataset from a verified export and an
/// explicit selection profile. Original export artifacts are never modified.
/// </summary>
public sealed class DatasetBuildService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

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

        await new FileSystemVoiceExportStore(exportRoot).RecoverPendingTransactionsAsync(cancellationToken).ConfigureAwait(false);
        await using var rootLock = await ExportRootLock.AcquireAsync(
            exportRoot,
            ExportRootLockMode.Exclusive,
            Guid.NewGuid().ToString("N"),
            runId: null,
            cancellationToken).ConfigureAwait(false);
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

        var outputRoot = Path.GetFullPath(request.OutputDirectory ?? Path.Combine(exportRoot, "datasets", profile.SelectionFingerprint));
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
        var usedHardLinks = true;
        try
        {
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
                var sourceMetadata = await VerifyArtifactAsync(sourcePath, entry.OriginalByteLength, entry.OriginalSha256, cancellationToken).ConfigureAwait(false);
                var relativeAudioPath = "audio/" + itemId + ".silk";
                var destinationPath = ExportPathSafety.CombineUnderRoot(stagingRoot, relativeAudioPath);
                if (!TryCreateHardLink(destinationPath, sourcePath))
                {
                    usedHardLinks = false;
                    TryDeleteFile(destinationPath);
                    await CopyAsync(sourcePath, destinationPath, cancellationToken).ConfigureAwait(false);
                }

                var outputMetadata = await VerifyArtifactAsync(destinationPath, sourceMetadata.ByteLength, sourceMetadata.Sha256, cancellationToken).ConfigureAwait(false);
                datasetItems.Add(new VoiceDatasetEntry(
                    itemId,
                    relativeAudioPath,
                    outputMetadata.Sha256,
                    outputMetadata.ByteLength,
                    entry.DurationMs,
                    entry.QualityFlags,
                    entry.TrainingEligibility,
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
                    profileOutputSha256),
                JsonOptions,
                cancellationToken).ConfigureAwait(false);

            Directory.Move(stagingRoot, outputRoot);
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
                usedHardLinks);
        }
        finally
        {
            TryDeleteDirectory(stagingRoot);
        }
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

        await using var rootLock = await ExportRootLock.AcquireAsync(
            exportRoot,
            ExportRootLockMode.Shared,
            Guid.NewGuid().ToString("N"),
            runId: null,
            cancellationToken).ConfigureAwait(false);
        try
        {
            var inputs = await LoadInputsAsync(
                exportRoot,
                request.ManifestPath,
                request.ProfilePath,
                cancellationToken).ConfigureAwait(false);
            outputRoot = Path.GetFullPath(request.OutputDirectory ?? Path.Combine(
                exportRoot,
                "datasets",
                inputs.Profile.SelectionFingerprint));
            EnsureUnderRoot(exportRoot, outputRoot);
            var existing = await TryVerifyExistingAsync(
                outputRoot,
                inputs.Profile,
                inputs.ManifestPath,
                inputs.ManifestSha256,
                inputs.ProfileSha256,
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

        await using (var rootLock = await ExportRootLock.AcquireAsync(
            exportRoot,
            ExportRootLockMode.Exclusive,
            Guid.NewGuid().ToString("N"),
            runId: null,
            cancellationToken).ConfigureAwait(false))
        {
            if ((File.GetAttributes(outputRoot) & FileAttributes.ReparsePoint) != 0)
            {
                return InvalidVerification(outputRoot, "dataset-reparse-point", null, "The curated dataset directory cannot be a reparse point.");
            }

            var inputs = await LoadInputsAsync(
                exportRoot,
                request.ManifestPath,
                request.ProfilePath,
                cancellationToken).ConfigureAwait(false);
            var selected = CreateSelectedItems(inputs);
            var expectedAudioPaths = selected
                .Select(static item => item.RelativeAudioPath.Replace('/', Path.DirectorySeparatorChar))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (HasUnexpectedAudioFiles(outputRoot, expectedAudioPaths))
            {
                return InvalidVerification(outputRoot, "dataset-audio-set-invalid", null, "The curated dataset contains missing, extra, or unsafe audio files.");
            }

            var datasetItems = new List<VoiceDatasetEntry>(selected.Count);
            foreach (var selectedItem in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = ExportPathSafety.CombineUnderRoot(outputRoot, selectedItem.RelativeAudioPath);
                var metadata = await VerifyArtifactAsync(
                    path,
                    selectedItem.Entry.OriginalByteLength,
                    selectedItem.Entry.OriginalSha256,
                    cancellationToken).ConfigureAwait(false);
                datasetItems.Add(new VoiceDatasetEntry(
                    selectedItem.ItemId,
                    selectedItem.RelativeAudioPath,
                    metadata.Sha256,
                    metadata.ByteLength,
                    selectedItem.Entry.DurationMs,
                    selectedItem.Entry.QualityFlags,
                    selectedItem.Entry.TrainingEligibility,
                    Selected: true));
            }

            var datasetManifest = new VoiceDatasetManifest(
                DateTimeOffset.UtcNow,
                inputs.Manifest.RunId,
                datasetItems);
            var profileOutputPath = Path.Combine(outputRoot, "selection-profile.json");
            var datasetManifestPath = Path.Combine(outputRoot, "dataset.json");
            var datasetCsvPath = Path.Combine(outputRoot, "dataset.csv");
            var buildManifestPath = Path.Combine(outputRoot, "build-manifest.json");
            await AtomicFileWriter.WriteJsonAsync(profileOutputPath, inputs.Profile, JsonOptions, cancellationToken).ConfigureAwait(false);
            await AtomicFileWriter.WriteJsonAsync(datasetManifestPath, datasetManifest, JsonOptions, cancellationToken).ConfigureAwait(false);
            await VoiceManifestCsvWriter.WritePortableAsync(datasetCsvPath, datasetManifest, cancellationToken).ConfigureAwait(false);
            var buildItems = selected
                .Zip(datasetItems, static (selectedItem, datasetItem) => new DatasetBuildItem(
                    selectedItem.ItemId,
                    selectedItem.RelativeAudioPath,
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
                    await FileHashing.ComputeSha256Async(datasetManifestPath, cancellationToken).ConfigureAwait(false),
                    await FileHashing.ComputeSha256Async(datasetCsvPath, cancellationToken).ConfigureAwait(false),
                    await FileHashing.ComputeSha256Async(profileOutputPath, cancellationToken).ConfigureAwait(false)),
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
        }

        return await VerifyAsync(
            new DatasetBuildRequest(
                request.ExportDirectory,
                request.ProfilePath,
                request.ManifestPath,
                request.OutputDirectory),
            cancellationToken).ConfigureAwait(false);
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

        EnsureDatasetNamespace(manifest);
        var entries = manifest.Entries.ToDictionary(
            entry => ExportItemIdentity.ComputeItemId(entry, manifest.DatasetNamespaceKey),
            StringComparer.OrdinalIgnoreCase);
        if (profile.SelectedItemIds.Any(itemId => !entries.ContainsKey(itemId)))
        {
            throw new AppFailureException(ErrorCode.InvalidRequest, "The selection profile references an item outside the export manifest.");
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
            Array.Empty<DatasetBuildVerificationIssue>());

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

    private static async Task<DatasetBuildResult?> TryVerifyExistingAsync(
        string outputRoot,
        DatasetSelectionProfile profile,
        string manifestPath,
        string manifestSha256,
        string profileSha256,
        CancellationToken cancellationToken)
    {
        if (Directory.Exists(outputRoot)
            && (File.GetAttributes(outputRoot) & FileAttributes.ReparsePoint) != 0)
        {
            return null;
        }

        var buildManifestPath = Path.Combine(outputRoot, "build-manifest.json");
        if (!Directory.Exists(outputRoot) || !File.Exists(buildManifestPath))
        {
            return null;
        }

        var build = await ReadAsync<DatasetBuildManifest>(buildManifestPath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(build.SelectionFingerprint, profile.SelectionFingerprint, StringComparison.Ordinal)
            || !string.Equals(build.SourceManifestSha256, manifestSha256, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(build.ProfileSha256, profileSha256, StringComparison.OrdinalIgnoreCase))
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
            || string.IsNullOrWhiteSpace(build.ProfileOutputSha256))
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
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var outputProfile = await ReadAsync<DatasetSelectionProfile>(profileOutputPath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(outputProfile.SelectionFingerprint, profile.SelectionFingerprint, StringComparison.Ordinal)
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
            UsedHardLinks: false);
    }

    private static async Task<T> ReadAsync<T>(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The dataset input document is empty.");
    }

    private static bool IsTrainingEligible(VoiceExportEntry entry, DatasetCurationFilters filters)
    {
        if (entry.ExportState == ExportState.Failed
            || entry.HasDecodeError
            || entry.DurationMs is null
            || filters.IncomingOnly && entry.Direction != VoiceDirection.Incoming
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

    private static async Task CopyAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(destination, 128 * 1024, cancellationToken).ConfigureAwait(false);
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
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
