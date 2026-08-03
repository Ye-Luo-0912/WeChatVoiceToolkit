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
    [ObservableProperty] private string? _minimumDurationMsText;
    [ObservableProperty] private string? _maximumDurationMsText;
    [ObservableProperty] private string? _minimumByteLengthText;
    [ObservableProperty] private string? _maximumByteLengthText;
    [ObservableProperty] private string? _excludedQualityFlagsText;
    [ObservableProperty] private bool _includeUnknownDuration;
    [ObservableProperty] private string? _curationSummary;
    [ObservableProperty] private string? _profileSummary;
    [ObservableProperty] private long _selectedDurationMs;
    [ObservableProperty] private long _selectedByteLength;
    [ObservableProperty] private int _selectedCount;

    public ObservableCollection<DatasetCurationItemViewModel> Items { get; } = [];

    private DatasetCurationResult? _result;
    private DatasetSelectionProfile? _profileToApply;

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

        var profile = new DatasetSelectionProfile(
            result.ManifestSha256,
            result.RunId,
            BuildFilters(),
            Items.Where(static item => item.IsSelected).Select(static item => item.ItemId).ToArray(),
            Items.Where(static item => item.IsDuplicateRepresentative).Select(static item => item.ItemId).ToArray());
        return RunHost.RunAsync(
            async (context, cancellationToken) =>
            {
                await Workflows.DatasetCuration.SaveProfileAsync(exportDirectory, profile, context, cancellationToken).ConfigureAwait(false);
                return profile;
            },
            saved => ProfileSummary = $"Selection Profile 已保存：{saved.SelectedItemIds.Count} 条，绑定 Manifest {Short(saved.ManifestSha256)}");
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
        IncludeUnknownDuration = profile.Filters.IncludeUnknownDuration;
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
        ProfileSummary = "已清除训练集选择；导出文件未修改。";
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
            IncludeUnknownDuration,
            IncomingOnly: true,
            (ExcludedQualityFlagsText ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private void ApplyResult(DatasetCurationResult result)
    {
        _result = result;
        Items.Clear();
        foreach (var item in result.Items)
        {
            Items.Add(new DatasetCurationItemViewModel(item, OnItemChanged));
        }

        SelectedDurationMs = result.SelectedDurationMs;
        SelectedByteLength = result.SelectedByteLength;
        SelectedCount = result.Items.Count(static item => item.IsSelected);
        CurationSummary = $"候选 {result.Items.Count(static item => item.PassesFilters)} 条；重复组 {result.DuplicateGroups.Count}；当前训练集 {SelectedCount} 条。成功导出不会自动进入训练集。";
        ProfileSummary = $"Manifest 已绑定：{Short(result.ManifestSha256)}；可保存 Selection Profile。";
        _profileToApply = null;
    }

    private void OnItemChanged(DatasetCurationItemViewModel changed)
    {
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
}

public sealed partial class DatasetCurationItemViewModel : ObservableObject
{
    private readonly Action<DatasetCurationItemViewModel> _changed;

    public DatasetCurationItemViewModel(DatasetCurationItem item, Action<DatasetCurationItemViewModel> changed)
    {
        _changed = changed;
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
    public bool CanSelect => PassesFilters;
    public string TrainingEligibility { get; }

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isDuplicateRepresentative;

    partial void OnIsSelectedChanged(bool value) => _changed(this);
}
