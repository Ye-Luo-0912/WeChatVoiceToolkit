using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WeChatVoice.Core.Models;
using WeChatVoice.Workflows.Workflows;

namespace WeChatVoice.Desktop.ViewModels;

/// <summary>
/// Storage management page: a read-only inventory of application-owned storage
/// and an explicit, previewed cleanup of only transient and expired-recoverable
/// objects. User assets (raw SILK exports, curated datasets) and verified
/// reusable workspaces are never auto-deleted here.
/// </summary>
public sealed partial class StorageViewModel : PageViewModelBase
{
    public StorageViewModel(DesktopServices services)
        : base(services)
    {
    }

    public override string Title => "存储管理";

    [ObservableProperty]
    private StorageInventorySummary? _inventory;

    [ObservableProperty]
    private IReadOnlyList<StorageAssetRecord> _assets = [];

    [ObservableProperty]
    private string _summaryText = "本页只显示应用自有存储的占用与可回收对象；原始 SILK 导出与数据集不会被自动删除。";

    [ObservableProperty]
    private string? _cleanupSummary;

    private StorageCleanupPreview? _cleanupPreview;
    private bool _cleanupArmed;
    private StorageCleanupPreview? _duplicateCleanupPreview;
    private bool _duplicateCleanupArmed;
    private StorageCleanupPreview? _snapshotCleanupPreview;
    private bool _snapshotCleanupArmed;

    public override async Task OnNavigatedToAsync(CancellationToken cancellationToken = default)
    {
        await base.OnNavigatedToAsync(cancellationToken).ConfigureAwait(false);
        if (Inventory is null)
        {
            await RefreshInventoryAsync().ConfigureAwait(false);
        }
    }

    [RelayCommand]
    private Task RefreshInventoryAsync()
    {
        _cleanupArmed = false;
        _cleanupPreview = null;
        _duplicateCleanupArmed = false;
        _duplicateCleanupPreview = null;
        _snapshotCleanupArmed = false;
        _snapshotCleanupPreview = null;
        return RunHost.RunAsync(
            async (context, cancellationToken) => await Workflows.StorageLifecycle.InventoryAsync(
                new StorageInventoryRequest(),
                context,
                cancellationToken).ConfigureAwait(false),
            summary =>
            {
                Inventory = summary;
                Assets = summary.Assets;
                SummaryText = FormatSummary(summary);
            });
    }

    [RelayCommand]
    private Task PreviewCleanupAsync()
    {
        _cleanupArmed = false;
        _cleanupPreview = null;
        return RunHost.RunAsync(
            async (context, cancellationToken) => await Workflows.StorageLifecycle.PreviewCleanupAsync(
                new StorageCleanupRequest(),
                context,
                cancellationToken).ConfigureAwait(false),
            preview =>
            {
                _cleanupPreview = preview;
                _cleanupArmed = true;
                CleanupSummary = $"预检将回收 {preview.ItemCount} 个临时/可恢复对象，共 {preview.TotalBytes} bytes。再次点击“执行清理”确认；验证过的 Workspace、原始导出与数据集不会被删除。";
            });
    }

    [RelayCommand]
    private Task CleanupAsync()
    {
        if (!_cleanupArmed || _cleanupPreview is null)
        {
            CleanupSummary = "请先执行清理预检，并确认预检结果。";
            return Task.CompletedTask;
        }

        _cleanupArmed = false;
        return RunHost.RunAsync(
            async (context, cancellationToken) => await Workflows.StorageLifecycle.CleanupAsync(
                new StorageCleanupRequest(),
                context,
                cancellationToken).ConfigureAwait(false),
            result =>
            {
                CleanupSummary = $"已回收 {result.DeletedCount} 个对象，共 {result.DeletedBytes} bytes；跳过 {result.SkippedReasons.Count} 项。";
                _cleanupPreview = null;
                Inventory = null;
                Assets = [];
                SummaryText = "清理完成，已重新扫描可用的占用数据。";
                _ = RefreshInventoryAsync();
            });
    }

    [RelayCommand]
    private Task PreviewDuplicateCleanupAsync()
    {
        _duplicateCleanupArmed = false;
        _duplicateCleanupPreview = null;
        return RunHost.RunAsync(
            async (context, cancellationToken) => await Workflows.StorageLifecycle.PreviewDuplicateSnapshotCleanupAsync(
                new StorageInventoryRequest(), context, cancellationToken).ConfigureAwait(false),
            preview =>
            {
                _duplicateCleanupPreview = preview;
                _duplicateCleanupArmed = true;
                CleanupSummary = preview.ItemCount == 0
                    ? "没有发现内容指纹重复的快照。"
                    : $"发现 {preview.ItemCount} 个重复快照副本，共 {FormatBytes(preview.TotalBytes)}。再次点击“清理重复快照”将保留每组最新副本。";
            });
    }

    [RelayCommand]
    private Task CleanupDuplicateAsync()
    {
        if (!_duplicateCleanupArmed || _duplicateCleanupPreview is null)
        {
            CleanupSummary = "请先执行重复快照预检。";
            return Task.CompletedTask;
        }

        _duplicateCleanupArmed = false;
        return RunHost.RunAsync(
            async (context, cancellationToken) => await Workflows.StorageLifecycle.CleanupDuplicateSnapshotsAsync(
                new StorageInventoryRequest(), context, cancellationToken).ConfigureAwait(false),
            result =>
            {
                CleanupSummary = $"已清理重复快照 {result.DeletedCount} 个，释放 {FormatBytes(result.DeletedBytes)}；跳过 {result.SkippedReasons.Count} 项。";
                _duplicateCleanupPreview = null;
                Inventory = null;
                Assets = [];
                _ = RefreshInventoryAsync();
            });
    }

    [RelayCommand]
    private Task PreviewOldSnapshotCleanupAsync()
    {
        _snapshotCleanupArmed = false;
        _snapshotCleanupPreview = null;
        return RunHost.RunAsync(
            async (context, cancellationToken) => await Workflows.StorageLifecycle.PreviewCleanupAsync(
                new StorageCleanupRequest(PruneOldSnapshots: true), context, cancellationToken).ConfigureAwait(false),
            preview =>
            {
                _snapshotCleanupPreview = preview;
                _snapshotCleanupArmed = true;
                CleanupSummary = preview.ItemCount == 0
                    ? "每个账号都只保留了最新快照，没有可清理的旧快照。"
                    : $"发现 {preview.ItemCount} 个旧快照，共 {FormatBytes(preview.TotalBytes)}。再次点击“清理旧快照”将每个账号只保留最新副本。";
            });
    }

    [RelayCommand]
    private Task CleanupOldSnapshotsAsync()
    {
        if (!_snapshotCleanupArmed || _snapshotCleanupPreview is null)
        {
            CleanupSummary = "请先执行旧快照预检。";
            return Task.CompletedTask;
        }

        _snapshotCleanupArmed = false;
        return RunHost.RunAsync(
            async (context, cancellationToken) => await Workflows.StorageLifecycle.CleanupAsync(
                new StorageCleanupRequest(PruneOldSnapshots: true), context, cancellationToken).ConfigureAwait(false),
            result =>
            {
                CleanupSummary = $"已清理旧快照 {result.DeletedCount} 个，释放 {FormatBytes(result.DeletedBytes)}；每个账号保留最新快照。";
                _snapshotCleanupPreview = null;
                Inventory = null;
                Assets = [];
                _ = RefreshInventoryAsync();
            });
    }

    private static string FormatSummary(StorageInventorySummary summary) =>
        $"快照 {FormatBytes(summary.SnapshotBytes)} · 明文 Workspace {FormatBytes(summary.WorkspaceBytes)} · "
        + $"导出 {FormatBytes(summary.ExportBytes)} · 数据集 {FormatBytes(summary.DatasetBytes)} · "
        + $"临时/可恢复 {FormatBytes(summary.TempBytes + summary.RecoverableBytes)} · "
        + $"可安全回收约 {FormatBytes(summary.SafelyReclaimableBytes)}";

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }

    public static string KindLabel(StorageAssetKind kind) => kind switch
    {
        StorageAssetKind.Transient => "临时",
        StorageAssetKind.RecoverableIntermediate => "可恢复",
        StorageAssetKind.ReusableIntermediate => "可复用",
        StorageAssetKind.UserAsset => "用户资产",
        StorageAssetKind.DerivedUserAsset => "派生数据集",
        _ => "未知",
    };
}
