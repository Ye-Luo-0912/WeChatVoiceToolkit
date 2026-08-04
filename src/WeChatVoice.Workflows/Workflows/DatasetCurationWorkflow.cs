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

    Task<DatasetBuildResult> BuildDatasetAsync(
        DatasetBuildRequest request,
        WorkflowContext context,
        CancellationToken cancellationToken);

    Task<DatasetBuildVerificationResult> VerifyDatasetAsync(
        DatasetBuildRequest request,
        WorkflowContext context,
        CancellationToken cancellationToken);

    Task<DatasetBuildVerificationResult> RepairDatasetAsync(
        DatasetBuildRepairRequest request,
        WorkflowContext context,
        CancellationToken cancellationToken);

    Task<DatasetDeletePreview> PreviewDeleteDatasetAsync(
        DatasetDeleteRequest request,
        WorkflowContext context,
        CancellationToken cancellationToken);

    Task<DatasetDeleteResult> DeleteDatasetAsync(
        DatasetDeleteRequest request,
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
    private readonly DatasetBuildService _datasetBuildService;

    public DatasetCurationWorkflow(
        DatasetSelectionProfileStore? profileStore = null,
        DatasetBuildService? datasetBuildService = null)
    {
        _profileStore = profileStore ?? new DatasetSelectionProfileStore();
        _datasetBuildService = datasetBuildService ?? new DatasetBuildService();
    }

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
            await _datasetBuildService.RecoverPendingMetadataTransactionsAsync(
                Path.GetFullPath(request.ExportDirectory),
                cancellationToken).ConfigureAwait(false);
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
            var exportRoot = Path.GetFullPath(exportDirectory);
            if (!Directory.Exists(exportRoot))
            {
                throw new AppFailureException(ErrorCode.InvalidRequest, "The export directory does not exist.");
            }
            await _profileStore.WriteAsync(exportRoot, profile, cancellationToken).ConfigureAwait(false);
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
            var exportRoot = Path.GetFullPath(exportDirectory);
            if (!Directory.Exists(exportRoot))
            {
                throw new AppFailureException(ErrorCode.InvalidRequest, "The export directory does not exist.");
            }
            var profile = await _profileStore.ReadAsync(exportRoot, cancellationToken).ConfigureAwait(false);
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

    public async Task<DatasetBuildResult> BuildDatasetAsync(
        DatasetBuildRequest request,
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
            context.Report(OperationPhase.VoiceExport, OperationStageIds.Completing, "构建训练数据集");
            if (request.Profile is not null)
            {
                var exportRoot = Path.GetFullPath(request.ExportDirectory);
                await _profileStore.WriteAsync(exportRoot, request.Profile, cancellationToken).ConfigureAwait(false);
                request = request with
                {
                    Profile = null,
                    ProfilePath = DatasetSelectionProfileStore.GetPath(exportRoot),
                };
            }

            var result = await _datasetBuildService.BuildAsync(request, cancellationToken).ConfigureAwait(false);
            context.StateMachine.TryComplete();
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

    public async Task<DatasetBuildVerificationResult> VerifyDatasetAsync(
        DatasetBuildRequest request,
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
            context.Report(OperationPhase.VoiceExport, OperationStageIds.LoadingWorkspace, "验证训练数据集");
            var result = await _datasetBuildService.VerifyAsync(request, cancellationToken).ConfigureAwait(false);
            context.StateMachine.TryComplete();
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

    public async Task<DatasetBuildVerificationResult> RepairDatasetAsync(
        DatasetBuildRepairRequest request,
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
            context.Report(OperationPhase.VoiceExport, OperationStageIds.Completing, "修复训练数据集元数据");
            var result = await _datasetBuildService.RepairAsync(request, cancellationToken).ConfigureAwait(false);
            context.StateMachine.TryComplete();
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

    public async Task<DatasetDeletePreview> PreviewDeleteDatasetAsync(
        DatasetDeleteRequest request,
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
            context.Report(OperationPhase.VoiceExport, OperationStageIds.LoadingWorkspace, "验证待删除训练数据集");
            var result = await _datasetBuildService.PreviewDeleteAsync(request, cancellationToken).ConfigureAwait(false);
            context.StateMachine.TryComplete();
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

    public async Task<DatasetDeleteResult> DeleteDatasetAsync(
        DatasetDeleteRequest request,
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
            context.Report(OperationPhase.VoiceExport, OperationStageIds.Completing, "删除训练数据集");
            var result = await _datasetBuildService.DeleteAsync(request, cancellationToken).ConfigureAwait(false);
            context.StateMachine.TryComplete();
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

    private static async Task<DatasetCurationResult> BuildAsync(
        DatasetCurationRequest request,
        CancellationToken cancellationToken)
    {
        var exportRoot = Path.GetFullPath(request.ExportDirectory);
        if (!Directory.Exists(exportRoot))
        {
            throw new AppFailureException(ErrorCode.InvalidRequest, "The export directory does not exist.");
        }

        await using var exportLock = await ExportRootLock.AcquireAsync(
            exportRoot,
            ExportRootLockMode.Shared,
            Guid.NewGuid().ToString("N"),
            runId: null,
            cancellationToken).ConfigureAwait(false);

        var manifestPath = Path.GetFullPath(request.ManifestPath ?? ResolveDefaultManifestPath(exportRoot));
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
        var itemIds = entries.Select(entry => ExportItemIdentity.ComputeItemId(entry, manifest.DatasetNamespaceKey)).ToHashSet(StringComparer.OrdinalIgnoreCase);
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
                var ids = group.Select(entry => ExportItemIdentity.ComputeItemId(entry, manifest.DatasetNamespaceKey)).ToArray();
                var selected = ids.Where(id => requestedSelected.Contains(id) || requestedRepresentatives.Contains(id)).ToArray();
                return new DatasetDuplicateGroup(
                    "duplicate-" + group.Key[..Math.Min(16, group.Key.Length)].ToLowerInvariant(),
                    group.Key,
                    ids,
                    selected.Length == 1 ? selected[0] : null);
            })
            .ToArray();

        var groupByItemId = new Dictionary<string, DatasetDuplicateGroup>(StringComparer.OrdinalIgnoreCase);
        foreach (var duplicateGroup in duplicateGroups)
        {
            foreach (var itemId in duplicateGroup.ItemIds)
            {
                groupByItemId[itemId] = duplicateGroup;
            }
        }

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
            var itemId = ExportItemIdentity.ComputeItemId(entry, manifest.DatasetNamespaceKey);
            DatasetDuplicateGroup? group = groupByItemId.TryGetValue(itemId, out var foundGroup)
                ? foundGroup
                : null;
            var passes = PassesFilters(entry, filters);
            var isRepresentative = group?.RepresentativeItemId is not null
                && string.Equals(group.RepresentativeItemId, itemId, StringComparison.OrdinalIgnoreCase);
            var isSelected = passes
                && entry.DurationMs is not null
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
            if (!filters.ShowUnknownDuration)
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

    private static string ResolveDefaultManifestPath(string exportRoot)
        => File.Exists(Path.Combine(exportRoot, ExportManifestLayout.PrivateManifestFileName))
            ? Path.Combine(exportRoot, ExportManifestLayout.PrivateManifestFileName)
            : Path.Combine(exportRoot, ExportManifestLayout.LegacyPortableManifestFileName);
}
