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
        DatasetOutputDirectory = services.Project.DatasetOutputDirectory;
    }

    public override string Title => "数据集整理";

    public override bool CanNavigate
        => Services.Project.LastExportRun is not null || HasReusableExport;

    public override string? NavigationHint => CanNavigate
        ? null
        : "请先完成一次 SILK 导出，再进入数据集整理";

    public bool HasCandidates => Items.Count > 0;

    public bool HasEligibleCandidates => Items.Any(static item => item.CanSelect);

    public bool HasSelection => SelectedCount > 0;

    public bool CanBuildDataset => CanStartOperation && _result is not null && HasSelection;

    public string DatasetOutputHint
        => string.IsNullOrWhiteSpace(DatasetOutputDirectory)
            ? "默认：导出目录\\datasets\\<构建指纹>（无需手动填写）"
            : $"数据集保存位置：{DatasetOutputDirectory}";

    public string CandidateReadinessHint
        => _result is null
            ? "进入本页后会自动读取最近一次导出的 SILK。"
            : HasEligibleCandidates
                ? "可以直接点击“全选可训练”，也可以逐条试听后勾选。"
                : "当前没有可直接训练的样本。未知时长需要先在“语音扫描”启用时长分析并重新扫描；原始 SILK 不会被修改。";

    public string DecoderHint
        => Services.Workflows.DecoderStatusReport.Status switch
        {
            DecoderStatus.Available => "解码器已就绪，可试听并分析时长。",
            DecoderStatus.Missing => "尚未配置 SILK 解码器；试听和时长分析暂不可用。",
            DecoderStatus.FailedSelfTest => "已配置的 SILK 解码器不可用，请在语音扫描页重新选择。",
            _ => "当前 SILK 解码器不可用，请在语音扫描页检查设置。",
        };

    private bool HasReusableExport
    {
        get
        {
            var root = Services.Project.ExportDirectory;
            return !string.IsNullOrWhiteSpace(root)
                && File.Exists(Path.Combine(root, ExportManifestLayout.PortableManifestFileName));
        }
    }

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

    public override async Task OnNavigatedToAsync(CancellationToken cancellationToken = default)
    {
        await base.OnNavigatedToAsync(cancellationToken).ConfigureAwait(false);
        ExportDirectory = Services.Project.ExportDirectory;
        DatasetOutputDirectory = Services.Project.DatasetOutputDirectory;
        OnPropertyChanged(nameof(DecoderHint));
        UpdatePreviewAvailability();
        if (CanNavigate && _result is null && !RunHost.IsRunning)
        {
            await LoadAsync().ConfigureAwait(false);
        }
    }

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
                Services.Project.DatasetOutputDirectory = result.OutputDirectory;
                Services.StoragePathRegistry.Register(result.OutputDirectory, StorageAssetKind.DerivedUserAsset);
                if (!string.IsNullOrWhiteSpace(Services.Project.WorkspacePath))
                {
                    Services.RecentWorkspaces.SetLastDatasetDirectory(Services.Project.WorkspacePath, result.OutputDirectory);
                }
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
                Services.Project.DatasetOutputDirectory = null;
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
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanBuildDataset));
    }

    [RelayCommand]
    private void SelectAllEligible()
    {
        foreach (var item in Items.Where(static item => item.CanSelect))
        {
            item.IsSelected = true;
        }

        UpdateTotals();
        SelectionDirty = true;
        ProfileSummary = $"已选择 {SelectedCount} 条可训练语音；重复组会自动保留一个代表项。";
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanBuildDataset));
    }

    [RelayCommand]
    private void StopPreview()
    {
        StopPreviewCore();
        ProfileSummary = "已停止试听。";
    }

    [RelayCommand]
    private void ConfigureDecoder()
        => Services.Navigation.NavigateTo(typeof(ScanViewModel));

    partial void OnExportDirectoryChanged(string? value)
    {
        if (!string.Equals(value, Services.Project.ExportDirectory, StringComparison.OrdinalIgnoreCase))
        {
            _result = null;
            _profileToApply = null;
            Items.Clear();
            OnPropertyChanged(nameof(HasCandidates));
            OnPropertyChanged(nameof(HasEligibleCandidates));
            OnPropertyChanged(nameof(CandidateReadinessHint));
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
            OnPropertyChanged(nameof(CanNavigate));
            OnPropertyChanged(nameof(HasReusableExport));
            OnPropertyChanged(nameof(DatasetOutputHint));
        }

        if (propertyName == nameof(ExportProjectSession.DatasetOutputDirectory))
        {
            DatasetOutputDirectory = Services.Project.DatasetOutputDirectory;
            OnPropertyChanged(nameof(DatasetOutputHint));
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
                stopPreview: StopPreviewAsync,
                previewAvailable: Services.Workflows.DecoderStatusReport.Status == DecoderStatus.Available));
        }

        SelectedDurationMs = result.SelectedDurationMs;
        SelectedByteLength = result.SelectedByteLength;
        SelectedCount = result.Items.Count(static item => item.IsSelected);
        var filteredCount = result.Items.Count(static item => item.PassesFilters);
        var unknownDurationCount = result.Items.Count(static item => item.DurationMs is null);
        CurationSummary = $"已读取 {result.Items.Count} 条 SILK：{filteredCount} 条符合筛选，{unknownDurationCount} 条时长未知，当前已选 {SelectedCount} 条。";
        ProfileSummary = $"Manifest 已绑定：{Short(result.ManifestSha256)}；Selection Fingerprint：{Short(result.SelectionFingerprint)}。";
        _profileToApply = null;
        SelectionDirty = false;
        OnPropertyChanged(nameof(HasCandidates));
        OnPropertyChanged(nameof(HasEligibleCandidates));
        OnPropertyChanged(nameof(CandidateReadinessHint));
    }

    private void UpdatePreviewAvailability()
    {
        var available = Services.Workflows.DecoderStatusReport.Status == DecoderStatus.Available;
        foreach (var item in Items)
        {
            item.SetPreviewAvailability(available);
        }
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
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanBuildDataset));
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

        if (Services.Workflows.DecoderStatusReport.Status != DecoderStatus.Available)
        {
            ProfileSummary = "试听需要可用的 SILK 解码器，请先点击“配置解码器”。";
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
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanBuildDataset));
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
        Func<DatasetCurationItemViewModel, Task>? stopPreview = null,
        bool previewAvailable = true)
    {
        _changed = changed;
        _preview = preview ?? (static (_, _) => Task.CompletedTask);
        _stopPreview = stopPreview ?? (static _ => Task.CompletedTask);
        _previewAvailable = previewAvailable;
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
    public bool CanPreview => IsPreviewing || _previewAvailable;

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isDuplicateRepresentative;
    private bool _previewAvailable;

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
        OnPropertyChanged(nameof(CanPreview));
    }

    internal void SetPreviewAvailability(bool value)
    {
        if (_previewAvailable == value)
        {
            return;
        }

        _previewAvailable = value;
        OnPropertyChanged(nameof(CanPreview));
    }
}
