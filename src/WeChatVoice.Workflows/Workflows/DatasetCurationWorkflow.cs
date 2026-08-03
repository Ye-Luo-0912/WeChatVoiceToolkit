using System.Text.Json;
using System.Text.Json.Serialization;
using WeChatVoice.Core.Errors;
using WeChatVoice.Core.Models;
using WeChatVoice.Infrastructure.Export;
using WeChatVoice.Infrastructure.Serialization;

namespace WeChatVoice.Workflows.Workflows;

public interface IDatasetCurationWorkflow
{
    Task<DatasetCurationResult> RunAsync(
        DatasetCurationRequest request,
        WorkflowContext context,
        CancellationToken cancellationToken);

    Task SaveProfileAsync(
        string exportDirectory,
        DatasetSelectionProfile profile,
        WorkflowContext context,
        CancellationToken cancellationToken);

    Task<DatasetSelectionProfile> LoadProfileAsync(
        string exportDirectory,
        WorkflowContext context,
        CancellationToken cancellationToken);
}

/// <summary>
/// Applies user curation policy to an already committed export manifest.  It
/// never writes SILK and never upgrades a successful export to a training item
/// without an explicit profile selection.
/// </summary>
public sealed class DatasetCurationWorkflow : IDatasetCurationWorkflow
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly DatasetSelectionProfileStore _profileStore;

    public DatasetCurationWorkflow(DatasetSelectionProfileStore? profileStore = null)
        => _profileStore = profileStore ?? new DatasetSelectionProfileStore();

    public async Task<DatasetCurationResult> RunAsync(
        DatasetCurationRequest request,
        WorkflowContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        if (!context.TryStart())
        {
            throw new InvalidOperationException("The workflow state machine is not idle.");
        }

        try
        {
            context.Report(OperationPhase.VoiceExport, OperationStageIds.LoadingWorkspace);
            var result = await BuildAsync(request, cancellationToken).ConfigureAwait(false);
            context.StateMachine.TryComplete();
            context.Report(OperationPhase.VoiceExport, OperationStageIds.Completing);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            context.StateMachine.TryCancel();
            throw;
        }
        catch
        {
            context.StateMachine.TryFail();
            throw;
        }
    }

    public async Task SaveProfileAsync(
        string exportDirectory,
        DatasetSelectionProfile profile,
        WorkflowContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(context);
        if (!context.TryStart())
        {
            throw new InvalidOperationException("The workflow state machine is not idle.");
        }

        try
        {
            context.Report(OperationPhase.VoiceExport, OperationStageIds.Completing, "保存数据集选择配置");
            await _profileStore.WriteAsync(exportDirectory, profile, cancellationToken).ConfigureAwait(false);
            context.StateMachine.TryComplete();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            context.StateMachine.TryCancel();
            throw;
        }
        catch
        {
            context.StateMachine.TryFail();
            throw;
        }
    }

    public async Task<DatasetSelectionProfile> LoadProfileAsync(
        string exportDirectory,
        WorkflowContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.TryStart())
        {
            throw new InvalidOperationException("The workflow state machine is not idle.");
        }

        try
        {
            context.Report(OperationPhase.VoiceExport, OperationStageIds.LoadingWorkspace, "加载数据集选择配置");
            var profile = await _profileStore.ReadAsync(exportDirectory, cancellationToken).ConfigureAwait(false);
            context.StateMachine.TryComplete();
            return profile;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            context.StateMachine.TryCancel();
            throw;
        }
        catch
        {
            context.StateMachine.TryFail();
            throw;
        }
    }

    private static async Task<DatasetCurationResult> BuildAsync(
        DatasetCurationRequest request,
        CancellationToken cancellationToken)
    {
        var exportRoot = Path.GetFullPath(request.ExportDirectory);
        if (!Directory.Exists(exportRoot))
        {
            throw new AppFailureException(ErrorCode.InvalidRequest, "The export directory does not exist.");
        }

        var manifestPath = Path.GetFullPath(request.ManifestPath ?? Path.Combine(exportRoot, "latest.manifest.json"));
        EnsureUnderRoot(exportRoot, manifestPath);
        if (!File.Exists(manifestPath))
        {
            throw new AppFailureException(ErrorCode.InvalidRequest, "The export manifest does not exist.");
        }

        var manifestSha256 = await FileHashing.ComputeSha256Async(manifestPath, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(request.ExpectedManifestSha256)
            && !string.Equals(manifestSha256, request.ExpectedManifestSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new AppFailureException(
                ErrorCode.InvalidRequest,
                "The selection profile is bound to a different export manifest; reload the current manifest before applying it.");
        }
        await using var stream = new FileStream(
            manifestPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var manifest = await JsonSerializer.DeserializeAsync<VoiceExportManifest>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The export manifest is empty.");

        var filters = request.Filters ?? new DatasetCurationFilters();
        var requestedSelected = ToIdSet(request.SelectedItemIds);
        var requestedRepresentatives = ToIdSet(request.DuplicateRepresentativeItemIds);
        var entries = manifest.Entries.ToArray();
        var itemIds = entries.Select(ExportItemIdentity.ComputeItemId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (requestedSelected.Except(itemIds, StringComparer.OrdinalIgnoreCase).Any()
            || requestedRepresentatives.Except(itemIds, StringComparer.OrdinalIgnoreCase).Any())
        {
            throw new AppFailureException(ErrorCode.InvalidRequest, "The curation profile contains an item that is not in the current export manifest.");
        }

        var duplicateGroups = entries
            .Where(static entry => !string.IsNullOrWhiteSpace(entry.OriginalSha256))
            .GroupBy(static entry => entry.OriginalSha256, StringComparer.OrdinalIgnoreCase)
            .Where(static group => group.Count() > 1)
            .Select(group =>
            {
                var ids = group.Select(ExportItemIdentity.ComputeItemId).ToArray();
                var selected = ids.Where(id => requestedSelected.Contains(id) || requestedRepresentatives.Contains(id)).ToArray();
                return new DatasetDuplicateGroup(
                    "duplicate-" + group.Key[..Math.Min(16, group.Key.Length)].ToLowerInvariant(),
                    group.Key,
                    ids,
                    selected.Length == 1 ? selected[0] : null);
            })
            .ToArray();

        foreach (var group in duplicateGroups)
        {
            var selected = group.ItemIds.Count(id => requestedSelected.Contains(id) || requestedRepresentatives.Contains(id));
            if (selected > 1)
            {
                throw new AppFailureException(ErrorCode.InvalidRequest, "Select only one representative from each duplicate group.");
            }
        }

        var items = new List<DatasetCurationItem>(entries.Length);
        long selectedDuration = 0;
        long selectedBytes = 0;
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var itemId = ExportItemIdentity.ComputeItemId(entry);
            var group = duplicateGroups.FirstOrDefault(candidate => candidate.ItemIds.Contains(itemId, StringComparer.OrdinalIgnoreCase));
            var passes = PassesFilters(entry, filters);
            var isRepresentative = group?.RepresentativeItemId is not null
                && string.Equals(group.RepresentativeItemId, itemId, StringComparison.OrdinalIgnoreCase);
            var isSelected = passes
                && (requestedSelected.Contains(itemId) || requestedRepresentatives.Contains(itemId));
            if (isSelected && group is not null && group.RepresentativeItemId is null)
            {
                isRepresentative = true;
            }

            if (isSelected)
            {
                selectedDuration = checked(selectedDuration + Math.Max(0, entry.DurationMs ?? 0));
                selectedBytes = checked(selectedBytes + Math.Max(0, entry.OriginalByteLength));
            }

            items.Add(new DatasetCurationItem(
                itemId,
                entry.OriginalPath,
                entry.OriginalSha256,
                entry.OriginalByteLength,
                entry.DurationMs,
                entry.QualityFlags,
                entry.Direction,
                group?.GroupId,
                group?.ItemIds.Count ?? 0,
                passes,
                isSelected,
                isRepresentative,
                passes
                    ? entry.DurationMs is null ? TrainingEligibility.Unknown : TrainingEligibility.Eligible
                    : TrainingEligibility.Rejected));
        }

        var profile = new DatasetSelectionProfile(
            manifestSha256,
            manifest.RunId,
            filters,
            items.Where(static item => item.IsSelected).Select(static item => item.ItemId).ToArray(),
            items.Where(static item => item.IsDuplicateRepresentative).Select(static item => item.ItemId).ToArray());
        return new DatasetCurationResult(
            exportRoot,
            manifestPath,
            manifestSha256,
            manifest.RunId,
            items,
            duplicateGroups,
            profile,
            selectedDuration,
            selectedBytes);
    }

    private static bool PassesFilters(VoiceExportEntry entry, DatasetCurationFilters filters)
    {
        if (entry.ExportState is ExportState.Failed
            || entry.HasDecodeError
            || filters.IncomingOnly && entry.Direction != VoiceDirection.Incoming)
        {
            return false;
        }

        if (entry.DurationMs is null)
        {
            if (!filters.IncludeUnknownDuration)
            {
                return false;
            }
        }
        else if (filters.MinimumDurationMs is not null && entry.DurationMs < filters.MinimumDurationMs
            || filters.MaximumDurationMs is not null && entry.DurationMs > filters.MaximumDurationMs)
        {
            return false;
        }

        if (filters.MinimumByteLength is not null && entry.OriginalByteLength < filters.MinimumByteLength
            || filters.MaximumByteLength is not null && entry.OriginalByteLength > filters.MaximumByteLength)
        {
            return false;
        }

        var flags = entry.QualityFlags.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return !filters.ExcludedQualityFlags.Any(flags.Contains);
    }

    private static HashSet<string> ToIdSet(IReadOnlyList<string>? values)
        => (values ?? Array.Empty<string>())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim().ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static void EnsureUnderRoot(string root, string path)
    {
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(root, path, StringComparison.OrdinalIgnoreCase))
        {
            throw new AppFailureException(ErrorCode.InvalidRequest, "The manifest path must remain inside the export directory.");
        }
    }
}
