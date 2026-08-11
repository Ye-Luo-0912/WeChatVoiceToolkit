using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WeChatVoice.Core.Errors;
using WeChatVoice.Core.Models;
using WeChatVoice.Desktop.Infrastructure;

namespace WeChatVoice.Desktop.ViewModels;

/// <summary>
/// Explicit dataset curation page.  It starts with no training selections;
/// filters only narrow the candidate view and a user click is required before
/// an item contributes to training totals.
/// </summary>
public sealed partial class DatasetCurationViewModel : PageViewModelBase
{
    public DatasetCurationViewModel(DesktopServices services)
        : base(services)
    {
        ExportDirectory = services.Project.ExportDirectory;
    }

    public override string Title => "数据集整理";

    public override bool CanNavigate
        => Services.Project.LastExportRun is not null
            && !string.IsNullOrWhiteSpace(Services.Project.ExportDirectory);

    public override string? NavigationHint => CanNavigate
        ? null
        : "请先完成一次 SILK 导出，再进入数据集整理";

    [ObservableProperty] private string? _exportDirectory;
    [ObservableProperty] private string? _datasetOutputDirectory;
    [ObservableProperty] private string? _minimumDurationMsText;
    [ObservableProperty] private string? _maximumDurationMsText;
    [ObservableProperty] private string? _minimumByteLengthText;
    [ObservableProperty] private string? _maximumByteLengthText;
    [ObservableProperty] private string? _excludedQualityFlagsText;
    [ObservableProperty] private bool _showUnknownDuration;
    [ObservableProperty] private DatasetDirectionScope _directionScope = DatasetDirectionScope.Incoming;
    [ObservableProperty] private int _sampleRate = AudioBuildProfile.DefaultSampleRate;
    [ObservableProperty] private bool _mono = true;
    [ObservableProperty] private bool _selectionDirty;

    /// <summary>Display labels for the direction scope combo box.</summary>
    public IReadOnlyList<string> DirectionScopes { get; } =
    [
        "仅接收",
        "仅发送",
        "双向",
    ];

    public int DirectionScopeIndex
    {
        get => DirectionScope switch
        {
            DatasetDirectionScope.Outgoing => 1,
            DatasetDirectionScope.Both => 2,
            _ => 0,
        };
        set => DirectionScope = value switch
        {
            1 => DatasetDirectionScope.Outgoing,
            2 => DatasetDirectionScope.Both,
            _ => DatasetDirectionScope.Incoming,
        };
    }

    public string? SampleRateText
    {
        get => SampleRate.ToString();
        set => SampleRate = int.TryParse(value, out var parsed) && parsed > 0 ? parsed : SampleRate;
    }
    [ObservableProperty] private string? _deleteConfirmationText;
    [ObservableProperty] private string? _curationSummary;
    [ObservableProperty] private string? _profileSummary;
    [ObservableProperty] private string? _buildSummary;
    [ObservableProperty] private long _selectedDurationMs;
    [ObservableProperty] private long _selectedByteLength;
    [ObservableProperty] private int _selectedCount;

    public ObservableCollection<DatasetCurationItemViewModel> Items { get; } = [];

    private DatasetCurationResult? _result;
    private DatasetSelectionProfile? _profileToApply;
    private DatasetCurationItemViewModel? _activePreview;

    [RelayCommand]
    private Task LoadAsync()
    {
        var exportDirectory = string.IsNullOrWhiteSpace(ExportDirectory)
            ? Services.Project.ExportDirectory
            : ExportDirectory;
        var profile = _profileToApply;
        var selectedIds = profile?.SelectedItemIds
            ?? Items.Where(static item => item.IsSelected).Select(static item => item.ItemId).ToArray();
        var representatives = profile?.DuplicateRepresentativeItemIds
            ?? Items.Where(static item => item.IsDuplicateRepresentative).Select(static item => item.ItemId).ToArray();
        return RunHost.RunAsync(
            async (context, cancellationToken) => await Workflows.DatasetCuration.RunAsync(
                new DatasetCurationRequest(
                    exportDirectory ?? throw new AppFailureException(ErrorCode.InvalidRequest, "Export directory is required."),
                    BuildFilters(),
                    selectedIds,
                    representatives,
                    ExpectedManifestSha256: profile?.ManifestSha256),
                context,
                cancellationToken).ConfigureAwait(false),
            ApplyResult);
    }

    [RelayCommand]
    private Task SaveProfileAsync()
    {
        var result = _result;
        var exportDirectory = ExportDirectory;
        if (result is null || string.IsNullOrWhiteSpace(exportDirectory))
        {
            ProfileSummary = "请先加载导出目录并完成筛选。";
            return Task.CompletedTask;
        }

        var profile = CreateCurrentProfile(result);
        return RunHost.RunAsync(
            async (context, cancellationToken) =>
            {
                await Workflows.DatasetCuration.SaveProfileAsync(exportDirectory, profile, context, cancellationToken).ConfigureAwait(false);
                return profile;
            },
            saved =>
            {
                SelectionDirty = false;
                ProfileSummary = $"Selection Profile 已保存：{saved.SelectedItemIds.Count} 条，Fingerprint {Short(saved.SelectionFingerprint)}";
            });
    }

    [RelayCommand]
    private Task BuildDatasetAsync()
    {
        var exportDirectory = string.IsNullOrWhiteSpace(ExportDirectory)
            ? Services.Project.ExportDirectory
            : ExportDirectory;
        var outputDirectory = DatasetOutputDirectory;
        var result = _result;
        if (result is null)
        {
            BuildSummary = "请先加载导出目录并完成筛选。";
            return Task.CompletedTask;
        }

        // Capture the current UI selection on the UI thread. The workflow
        // persists this exact profile before it reads any build inputs.
        var profile = CreateCurrentProfile(result);
        var audioProfile = CreateAudioProfile();
        return RunHost.RunAsync<DatasetBuildResult>(
            async (context, cancellationToken) => await Workflows.DatasetCuration.BuildDatasetAsync(
                new DatasetBuildRequest(
                    exportDirectory ?? throw new AppFailureException(ErrorCode.InvalidRequest, "Export directory is required."),
                    OutputDirectory: string.IsNullOrWhiteSpace(outputDirectory) ? null : outputDirectory,
                    Profile: profile,
                    AudioProfile: audioProfile),
                context,
                cancellationToken).ConfigureAwait(false),
            result =>
            {
                DatasetOutputDirectory = result.OutputDirectory;
                SelectionDirty = false;
                BuildSummary = result.LinkMode == DatasetLinkMode.LinkedView
                    ? $"训练数据集已构建：{result.ItemCount} 条，{result.TotalDurationMs} ms，{result.TotalByteLength} bytes。警告：这是非独立硬链接视图，不是便携副本。"
                    : $"训练数据集已构建：{result.ItemCount} 条，{result.TotalDurationMs} ms，{result.TotalByteLength} bytes。";
                if (result.AudioProfileFingerprint is not null)
                {
                    BuildSummary += $" 音频 Profile：{Short(result.AudioProfileFingerprint)}；Decoder：{result.DecoderIdentity ?? "未知"}。";
                }
            });
    }

    [RelayCommand]
    private Task DeleteDatasetAsync()
    {
        var exportDirectory = string.IsNullOrWhiteSpace(ExportDirectory)
            ? Services.Project.ExportDirectory
            : ExportDirectory;
        var outputDirectory = DatasetOutputDirectory;
        var result = _result;
        if (string.IsNullOrWhiteSpace(exportDirectory)
            || string.IsNullOrWhiteSpace(outputDirectory)
            || result is null)
        {
            BuildSummary = "请先加载并构建训练数据集。";
            return Task.CompletedTask;
        }

        var request = new DatasetDeleteRequest(
            exportDirectory,
            outputDirectory,
            result.SelectionFingerprint,
            string.Equals(DeleteConfirmationText?.Trim(), "DELETE", StringComparison.Ordinal));
        return RunHost.RunAsync<DatasetDeleteResult>(
            async (context, cancellationToken) => await Workflows.DatasetCuration.DeleteDatasetAsync(request, context, cancellationToken).ConfigureAwait(false),
            deleted =>
            {
                DatasetOutputDirectory = null;
                DeleteConfirmationText = null;
                BuildSummary = $"已删除派生训练数据集：{deleted.ItemCount} 条，{deleted.TotalByteLength} bytes；原始 Export 未修改。";
            });
    }

    [RelayCommand]
    private Task VerifyDatasetAsync()
    {
        var exportDirectory = string.IsNullOrWhiteSpace(ExportDirectory)
            ? Services.Project.ExportDirectory
            : ExportDirectory;
        var outputDirectory = DatasetOutputDirectory;
        return RunHost.RunAsync<DatasetBuildVerificationResult>(
            async (context, cancellationToken) => await Workflows.DatasetCuration.VerifyDatasetAsync(
                new DatasetBuildRequest(
                    exportDirectory ?? throw new AppFailureException(ErrorCode.InvalidRequest, "Export directory is required."),
                    OutputDirectory: outputDirectory),
                context,
                cancellationToken).ConfigureAwait(false),
            result => BuildSummary = result.IsValid
                ? $"训练数据集验证通过：{result.ItemCount} 条。"
                : $"训练数据集验证失败：{string.Join(", ", result.Issues.Select(static issue => issue.Code))}");
    }

    [RelayCommand]
    private Task RepairDatasetAsync()
    {
        var exportDirectory = string.IsNullOrWhiteSpace(ExportDirectory)
            ? Services.Project.ExportDirectory
            : ExportDirectory;
        var outputDirectory = DatasetOutputDirectory;
        return RunHost.RunAsync<DatasetBuildVerificationResult>(
            async (context, cancellationToken) => await Workflows.DatasetCuration.RepairDatasetAsync(
                new DatasetBuildRepairRequest(
                    exportDirectory ?? throw new AppFailureException(ErrorCode.InvalidRequest, "Export directory is required."),
                    outputDirectory ?? throw new AppFailureException(ErrorCode.InvalidRequest, "Dataset output directory is required.")),
                context,
                cancellationToken).ConfigureAwait(false),
            result => BuildSummary = result.IsValid
                ? $"训练数据集元数据已修复：{result.ItemCount} 条；SILK 未修改。"
                : $"训练数据集修复失败：{string.Join(", ", result.Issues.Select(static issue => issue.Code))}");
    }

    [RelayCommand]
    private async Task LoadProfileAsync()
    {
        var exportDirectory = string.IsNullOrWhiteSpace(ExportDirectory)
            ? Services.Project.ExportDirectory
            : ExportDirectory;
        if (string.IsNullOrWhiteSpace(exportDirectory))
        {
            ProfileSummary = "导出目录不能为空。";
            return;
        }

        DatasetSelectionProfile? profile = null;
        await RunHost.RunAsync(
            async (context, cancellationToken) => await Workflows.DatasetCuration.LoadProfileAsync(exportDirectory, context, cancellationToken).ConfigureAwait(false),
            loaded => profile = loaded).ConfigureAwait(true);
        if (profile is null) return;

        MinimumDurationMsText = Format(profile.Filters.MinimumDurationMs);
        MaximumDurationMsText = Format(profile.Filters.MaximumDurationMs);
        MinimumByteLengthText = Format(profile.Filters.MinimumByteLength);
        MaximumByteLengthText = Format(profile.Filters.MaximumByteLength);
        ShowUnknownDuration = profile.Filters.ShowUnknownDuration;
        DirectionScope = profile.Filters.DirectionScope ?? DatasetDirectionScope.Both;
        ExcludedQualityFlagsText = string.Join(',', profile.Filters.ExcludedQualityFlags);
        _profileToApply = profile;
        ProfileSummary = $"已加载 Profile：{profile.SelectedItemIds.Count} 条；重新加载以验证 Manifest 绑定。";
        await LoadAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void ClearSelections()
    {
        foreach (var item in Items) item.IsSelected = false;
        UpdateTotals();
        SelectionDirty = true;
        ProfileSummary = "已清除训练集选择；导出文件未修改。";
    }

    [RelayCommand]
    private void StopPreview()
    {
        StopPreviewCore();
        ProfileSummary = "已停止试听。";
    }

    partial void OnExportDirectoryChanged(string? value)
    {
        if (!string.Equals(value, Services.Project.ExportDirectory, StringComparison.OrdinalIgnoreCase))
        {
            _result = null;
            _profileToApply = null;
            Items.Clear();
        }
    }

    partial void OnMinimumDurationMsTextChanged(string? value) => SelectionDirty = true;
    partial void OnMaximumDurationMsTextChanged(string? value) => SelectionDirty = true;
    partial void OnMinimumByteLengthTextChanged(string? value) => SelectionDirty = true;
    partial void OnMaximumByteLengthTextChanged(string? value) => SelectionDirty = true;
    partial void OnExcludedQualityFlagsTextChanged(string? value) => SelectionDirty = true;
    partial void OnShowUnknownDurationChanged(bool value) => SelectionDirty = true;

    protected override void OnProjectPropertyChanged(string? propertyName)
    {
        if (propertyName is nameof(ExportProjectSession.ExportDirectory)
            or nameof(ExportProjectSession.LastExportRun))
        {
            ExportDirectory = Services.Project.ExportDirectory;
        }
    }

    private DatasetCurationFilters BuildFilters()
        => new(
            ParseNonNegative(MinimumDurationMsText, "minimum duration"),
            ParseNonNegative(MaximumDurationMsText, "maximum duration"),
            ParseNonNegative(MinimumByteLengthText, "minimum byte length"),
            ParseNonNegative(MaximumByteLengthText, "maximum byte length"),
            ShowUnknownDuration,
            IncomingOnly: DirectionScope == DatasetDirectionScope.Incoming,
            (ExcludedQualityFlagsText ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            DirectionScope: DirectionScope);

    private AudioBuildProfile CreateAudioProfile()
        => new(
            SampleRate: SampleRate,
            Mono: Mono);

    private void ApplyResult(DatasetCurationResult result)
    {
        _result = result;
        Items.Clear();
        foreach (var item in result.Items)
        {
            Items.Add(new DatasetCurationItemViewModel(
                item,
                OnItemChanged,
                preview: PreviewItemAsync,
                stopPreview: StopPreviewAsync));
        }

        SelectedDurationMs = result.SelectedDurationMs;
        SelectedByteLength = result.SelectedByteLength;
        SelectedCount = result.Items.Count(static item => item.IsSelected);
        CurationSummary = $"候选 {result.Items.Count(static item => item.PassesFilters)} 条；重复组 {result.DuplicateGroups.Count}；当前训练集 {SelectedCount} 条。成功导出不会自动进入训练集。";
        ProfileSummary = $"Manifest 已绑定：{Short(result.ManifestSha256)}；Selection Fingerprint：{Short(result.SelectionFingerprint)}。";
        _profileToApply = null;
        SelectionDirty = false;
    }

    private void OnItemChanged(DatasetCurationItemViewModel changed)
    {
        SelectionDirty = true;
        if (!changed.IsSelected)
        {
            changed.IsDuplicateRepresentative = false;
            UpdateTotals();
            return;
        }

        if (!changed.PassesFilters)
        {
            changed.IsSelected = false;
            ProfileSummary = "未通过当前筛选的项目不能加入训练集。";
            return;
        }

        if (changed.IsSelected && !string.IsNullOrWhiteSpace(changed.DuplicateGroupId))
        {
            foreach (var other in Items.Where(item => !ReferenceEquals(item, changed)
                         && string.Equals(item.DuplicateGroupId, changed.DuplicateGroupId, StringComparison.OrdinalIgnoreCase)))
            {
                other.IsSelected = false;
                other.IsDuplicateRepresentative = false;
            }

            changed.IsDuplicateRepresentative = true;
        }

        UpdateTotals();
    }

    private async Task PreviewItemAsync(DatasetCurationItemViewModel item, CancellationToken cancellationToken)
    {
        var exportDirectory = string.IsNullOrWhiteSpace(ExportDirectory)
            ? Services.Project.ExportDirectory
            : ExportDirectory;
        if (string.IsNullOrWhiteSpace(exportDirectory))
        {
            ProfileSummary = "导出目录不能为空，无法试听。";
            return;
        }

        StopPreviewCore();
        var profile = CreateAudioProfile();
        DatasetPreviewDecodeResult? decoded = null;
        await RunHost.RunAsync(
            async (context, token) => await Workflows.DatasetCuration.PreviewDecodeAsync(
                exportDirectory,
                item.ItemId,
                profile.SampleRate,
                context,
                token).ConfigureAwait(false),
            result => decoded = result).ConfigureAwait(true);
        if (decoded is null)
        {
            return;
        }

        Services.AudioPreview.Play(decoded.DecodedWavPath);
        _activePreview = item;
        item.SetPreviewing(decoded.DecodedWavPath);
        ProfileSummary = $"正在试听 {Short(item.ItemId)}：{decoded.DurationMs} ms，{decoded.ByteLength} bytes。";
    }

    private async Task StopPreviewAsync(DatasetCurationItemViewModel item)
    {
        StopPreviewCore();
        await Task.CompletedTask.ConfigureAwait(true);
    }

    private void StopPreviewCore()
    {
        Services.AudioPreview.Stop();
        if (_activePreview is not null && _activePreview.PreviewWavPath is { } path)
        {
            _activePreview.SetPreviewing(null);
            TryDeletePreviewFile(path);
        }

        _activePreview = null;
    }

    private static void TryDeletePreviewFile(string path)
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

    private void UpdateTotals()
    {
        SelectedCount = Items.Count(static item => item.IsSelected);
        SelectedDurationMs = Items.Where(static item => item.IsSelected).Sum(static item => Math.Max(0, item.DurationMs ?? 0));
        SelectedByteLength = Items.Where(static item => item.IsSelected).Sum(static item => Math.Max(0, item.ByteLength));
    }

    private static long? ParseNonNegative(string? text, string label)
        => string.IsNullOrWhiteSpace(text)
            ? null
            : long.TryParse(text, out var value) && value >= 0
                ? value
                : throw new AppFailureException(ErrorCode.InvalidRequest, $"{label} must be a non-negative integer.");

    private static string? Format(long? value) => value?.ToString();

    private static string Short(string value) => value.Length <= 16 ? value : value[..16] + "…";

    private DatasetSelectionProfile CreateCurrentProfile(DatasetCurationResult result)
        => new(
            result.ManifestSha256,
            result.RunId,
            BuildFilters(),
            Items.Where(static item => item.IsSelected).Select(static item => item.ItemId).ToArray(),
            Items.Where(static item => item.IsDuplicateRepresentative).Select(static item => item.ItemId).ToArray());
}

public sealed partial class DatasetCurationItemViewModel : ObservableObject
{
    private readonly Action<DatasetCurationItemViewModel> _changed;
    private readonly Func<DatasetCurationItemViewModel, CancellationToken, Task> _preview;
    private readonly Func<DatasetCurationItemViewModel, Task> _stopPreview;

    public DatasetCurationItemViewModel(
        DatasetCurationItem item,
        Action<DatasetCurationItemViewModel> changed,
        Func<DatasetCurationItemViewModel, CancellationToken, Task>? preview = null,
        Func<DatasetCurationItemViewModel, Task>? stopPreview = null)
    {
        _changed = changed;
        _preview = preview ?? (static (_, _) => Task.CompletedTask);
        _stopPreview = stopPreview ?? (static _ => Task.CompletedTask);
        ItemId = item.ItemId;
        RelativeAudioPath = item.RelativeAudioPath;
        Sha256 = item.Sha256;
        ByteLength = item.ByteLength;
        DurationMs = item.DurationMs;
        QualityFlags = string.Join('|', item.QualityFlags);
        Direction = item.Direction.ToString();
        DuplicateGroupId = item.DuplicateGroupId;
        DuplicateGroupSize = item.DuplicateGroupSize;
        PassesFilters = item.PassesFilters;
        _isSelected = item.IsSelected;
        _isDuplicateRepresentative = item.IsDuplicateRepresentative;
        TrainingEligibility = item.TrainingEligibility.ToString();
    }

    public string ItemId { get; }
    public string RelativeAudioPath { get; }
    public string Sha256 { get; }
    public long ByteLength { get; }
    public long? DurationMs { get; }
    public string DurationText => DurationMs?.ToString() ?? "未知";
    public string QualityFlags { get; }
    public string Direction { get; }
    public string? DuplicateGroupId { get; }
    public int DuplicateGroupSize { get; }
    public bool PassesFilters { get; }
    public bool CanSelect => PassesFilters && DurationMs is not null && TrainingEligibility == nameof(WeChatVoice.Core.Models.TrainingEligibility.Eligible);
    public string TrainingEligibility { get; }

    /// <summary>True while this item's decoded preview is playing.</summary>
    public bool IsPreviewing { get; private set; }

    /// <summary>Transient decoded WAV owned by this item; deleted on stop/replace.</summary>
    public string? PreviewWavPath { get; private set; }

    public string PreviewButtonText => IsPreviewing ? "停止" : "试听";

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isDuplicateRepresentative;

    partial void OnIsSelectedChanged(bool value) => _changed(this);

    [RelayCommand]
    private async Task PreviewAsync()
    {
        if (IsPreviewing)
        {
            await _stopPreview(this).ConfigureAwait(true);
            return;
        }

        await _preview(this, default).ConfigureAwait(true);
    }

    internal void SetPreviewing(string? wavPath)
    {
        PreviewWavPath = wavPath;
        IsPreviewing = wavPath is not null;
        OnPropertyChanged(nameof(IsPreviewing));
        OnPropertyChanged(nameof(PreviewWavPath));
        OnPropertyChanged(nameof(PreviewButtonText));
    }
}
