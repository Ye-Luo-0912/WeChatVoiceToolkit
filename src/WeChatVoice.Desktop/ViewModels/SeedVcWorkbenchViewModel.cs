using System.Diagnostics;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WeChatVoice.Core.Models;
using WeChatVoice.Desktop.Infrastructure;

namespace WeChatVoice.Desktop.ViewModels;

/// <summary>
/// Small, reusable Seed-VC panel hosted by the Dataset page. It owns no
/// dataset logic: all validation, reuse and process orchestration remain in
/// the shared SeedVc workflow.
/// </summary>
public sealed partial class SeedVcWorkbenchViewModel : ObservableObject
{
    private readonly DesktopServices _services;
    private readonly Func<Action, Task> _invokeOnUi;
    private readonly SeedVcSettingsStore _settingsStore;
    private SeedVcPrepareResult? _prepared;
    private string? _datasetBuildFingerprint;
    private string? _lastRunDirectory;
    private string? _lastInferDirectory;
    private bool _restoringSettings;

    public SeedVcWorkbenchViewModel(DesktopServices services)
    {
        _services = services;
        _invokeOnUi = services.InvokeOnUi ?? (action => Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(action).GetTask());
        _settingsStore = new SeedVcSettingsStore(services.RecentWorkspaces.StorageDirectory);
        RunHost = new WorkflowRunHost(_invokeOnUi, services.Log, services.OperationCoordinator);
        PythonPath = "python";
        SeedVcRoot = Environment.GetEnvironmentVariable("WECHATVOICE_SEEDVC_ROOT");
    }

    public WorkflowRunHost RunHost { get; }

    [ObservableProperty] private string? _datasetDirectory;
    [ObservableProperty] private string? _anchorDirectory;
    [ObservableProperty] private string? _prepDirectory;
    [ObservableProperty] private string? _seedVcRoot;
    [ObservableProperty] private string? _pythonPath;
    [ObservableProperty] private string? _runName;
    [ObservableProperty] private string? _checkpointPath;
    [ObservableProperty] private string? _sourceAudioPath;
    [ObservableProperty] private string? _referenceAudioPath;
    [ObservableProperty] private string? _inferOutputPath;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private int _batchSize = 1;
    [ObservableProperty] private int _maxSteps = 1000;
    [ObservableProperty] private int _saveEvery = 500;
    [ObservableProperty] private int _diffusionSteps = 50;
    [ObservableProperty] private bool _isReady;

    public string PreparedSummary => _prepared is null
        ? "尚未准备 Seed‑VC 数据"
        : $"已准备 {_prepared.KeptCount} 条 · {_prepared.TotalDurationMs / 1000d:0.0} 秒 · 指纹 {_prepared.PrepFingerprint[..Math.Min(12, _prepared.PrepFingerprint.Length)]}";

    public string TrainSummary => LastTrain is null
        ? "尚未启动训练"
        : LastTrain.Status == SeedVcTrainStatus.Completed
            ? $"训练完成 · checkpoint {LastTrain.Checkpoints.Count} 个"
            : $"训练状态：{LastTrain.Status}";

    public string InferSummary => LastInfer is null
        ? "可选：使用训练 checkpoint 试听转换"
        : LastInfer.Status == SeedVcInferStatus.Completed ? $"转换完成：{LastInfer.OutputPath}" : $"转换状态：{LastInfer.Status}";

    public SeedVcTrainResult? LastTrain { get; private set; }
    public SeedVcInferResult? LastInfer { get; private set; }
    public SeedVcDoctorReport? LastDoctor { get; private set; }
    public string? LastRunDirectory => _lastRunDirectory;
    public string? LastInferDirectory => _lastInferDirectory;

    public bool CanPrepare => !string.IsNullOrWhiteSpace(DatasetDirectory) && !RunHost.IsRunning && !_services.OperationCoordinator.IsBusy;
    public bool CanTrain => !string.IsNullOrWhiteSpace(PrepDirectory) && !string.IsNullOrWhiteSpace(SeedVcRoot) && !RunHost.IsRunning && !_services.OperationCoordinator.IsBusy;
    public bool CanInfer => !string.IsNullOrWhiteSpace(SeedVcRoot) && !string.IsNullOrWhiteSpace(CheckpointPath) && !string.IsNullOrWhiteSpace(SourceAudioPath) && !string.IsNullOrWhiteSpace(ReferenceAudioPath) && !RunHost.IsRunning && !_services.OperationCoordinator.IsBusy;

    public void SetDataset(string? directory)
    {
        DatasetDirectory = string.IsNullOrWhiteSpace(directory) ? null : Path.GetFullPath(directory);
        var fingerprint = ReadDatasetFingerprint(DatasetDirectory);
        if (!string.Equals(_datasetBuildFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            _prepared = null;
            PrepDirectory = null;
            _datasetBuildFingerprint = fingerprint;
            RestoreSettings(fingerprint);
        }
        if (!string.IsNullOrWhiteSpace(DatasetDirectory) && string.IsNullOrWhiteSpace(PrepDirectory))
        {
            PrepDirectory = FindReusablePreparation(DatasetDirectory);
            if (PrepDirectory is not null)
            {
                StatusMessage = "发现已验证的 Seed‑VC 准备结果，可直接继续训练。";
            }
        }
        OnPropertyChanged(nameof(CanPrepare));
        OnPropertyChanged(nameof(CanTrain));
    }

    private void RestoreSettings(string? fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint)) return;
        var saved = _settingsStore.Load(fingerprint);
        if (saved is null) return;
        _restoringSettings = true;
        try
        {
            if (!string.IsNullOrWhiteSpace(saved.SeedVcRoot)) SeedVcRoot = saved.SeedVcRoot;
            if (!string.IsNullOrWhiteSpace(saved.PythonPath)) PythonPath = saved.PythonPath;
            AnchorDirectory = saved.AnchorDirectory;
            PrepDirectory = IsReusablePreparation(saved.PrepDirectory, fingerprint) ? saved.PrepDirectory : null;
            CheckpointPath = IsUsableFile(saved.CheckpointPath) ? saved.CheckpointPath : null;
            RunName = saved.RunName;
            SourceAudioPath = IsUsableFile(saved.SourceAudioPath) ? saved.SourceAudioPath : null;
            ReferenceAudioPath = IsUsableFile(saved.ReferenceAudioPath) ? saved.ReferenceAudioPath : null;
            InferOutputPath = !string.IsNullOrWhiteSpace(saved.InferOutputPath) ? saved.InferOutputPath : null;
            _lastRunDirectory = Directory.Exists(saved.LastRunDirectory) ? saved.LastRunDirectory : null;
            LastTrain = TryReadTrainResult(_lastRunDirectory);
            _lastInferDirectory = Directory.Exists(saved.LastInferDirectory) ? saved.LastInferDirectory : null;
            LastInfer = TryReadInferResult(_lastInferDirectory);
            OnPropertyChanged(nameof(LastRunDirectory));
            OnPropertyChanged(nameof(LastInferDirectory));
            OnPropertyChanged(nameof(TrainSummary));
            OnPropertyChanged(nameof(InferSummary));
            if (PrepDirectory is not null) StatusMessage = "已恢复上次验证过的 Seed‑VC 准备结果。";
        }
        finally { _restoringSettings = false; }
    }

    private void PersistSettings()
    {
        if (_restoringSettings || string.IsNullOrWhiteSpace(_datasetBuildFingerprint)) return;
        _settingsStore.Save(new SeedVcSettings(
            _datasetBuildFingerprint!, SeedVcRoot, PythonPath, AnchorDirectory,
            PrepDirectory, LastTrain?.RunDirectory ?? _lastRunDirectory, CheckpointPath, RunName,
            SourceAudioPath, ReferenceAudioPath, InferOutputPath, LastInfer?.RunDirectory ?? _lastInferDirectory));
    }

    [RelayCommand]
    private async Task PickSeedVcRootAsync()
    {
        var path = await _services.FolderPicker.PickFolderAsync("选择 Seed‑VC 安装目录").ConfigureAwait(true);
        if (path is not null) SeedVcRoot = path;
    }

    [RelayCommand]
    private async Task PickAnchorDirectoryAsync()
    {
        var path = await _services.FolderPicker.PickFolderAsync("选择手机朗读目录（可选）").ConfigureAwait(true);
        if (path is not null) AnchorDirectory = path;
    }

    [RelayCommand]
    private async Task PickCheckpointAsync()
    {
        var path = await _services.FolderPicker.PickFileAsync("选择 Seed‑VC checkpoint").ConfigureAwait(true);
        if (path is not null) CheckpointPath = path;
    }

    [RelayCommand]
    private async Task PickSourceAudioAsync()
    {
        var path = await _services.FolderPicker.PickFileAsync("选择源音频").ConfigureAwait(true);
        if (path is not null) SourceAudioPath = path;
    }

    [RelayCommand]
    private async Task PickReferenceAudioAsync()
    {
        var path = await _services.FolderPicker.PickFileAsync("选择参考音频").ConfigureAwait(true);
        if (path is not null) ReferenceAudioPath = path;
    }

    [RelayCommand]
    private Task DoctorAsync()
        => RunHost.RunAsync<SeedVcDoctorReport>(
            async (context, token) => await _services.Workflows.SeedVc.DoctorAsync(new SeedVcDoctorRequest(SeedVcRoot, PythonPath), context, token).ConfigureAwait(false),
            report =>
            {
                LastDoctor = report;
                IsReady = report.IsReady;
                StatusMessage = report.IsReady ? $"环境就绪：Python {report.PythonVersion ?? "未知"} · Torch {report.TorchVersion ?? "未知"}" : $"环境未就绪：{string.Join(", ", report.Issues)}";
                OnPropertyChanged(nameof(IsReady));
            });

    [RelayCommand]
    private Task PrepareAsync()
    {
        if (!CanPrepare) { StatusMessage = "请先完成训练数据集构建。"; return Task.CompletedTask; }
        return RunHost.RunAsync<SeedVcPrepareResult>(
            async (context, token) => await _services.Workflows.SeedVc.PrepareAsync(new SeedVcPrepareRequest(DatasetDirectory!, AnchorDirectory), context, token).ConfigureAwait(false),
            result =>
            {
                _prepared = result;
                PrepDirectory = result.OutputDirectory;
                PersistSettings();
                StatusMessage = result.Reused ? "已复用已验证的 Seed‑VC 准备结果。" : "Seed‑VC 训练数据准备完成。";
                OnPropertyChanged(nameof(PreparedSummary)); OnPropertyChanged(nameof(CanTrain));
            });
    }

    [RelayCommand]
    private Task TrainAsync()
    {
        if (!CanTrain) { StatusMessage = "请先检查环境并准备训练数据。"; return Task.CompletedTask; }
        return RunHost.RunAsync<SeedVcTrainResult>(
            async (context, token) => await _services.Workflows.SeedVc.TrainAsync(new SeedVcTrainRequest(PrepDirectory!, SeedVcRoot!, PythonPath, RunName: RunName, BatchSize: BatchSize, MaxSteps: MaxSteps, SaveEvery: SaveEvery), context, token).ConfigureAwait(false),
            result =>
            {
                LastTrain = result;
                _lastRunDirectory = result.RunDirectory;
                OnPropertyChanged(nameof(LastRunDirectory));
                if (result.Checkpoints.Count > 0) CheckpointPath = Path.Combine(result.RunDirectory, result.Checkpoints[^1].RelativePath.Replace('/', Path.DirectorySeparatorChar));
                PersistSettings();
                StatusMessage = TrainSummary;
                OnPropertyChanged(nameof(TrainSummary)); OnPropertyChanged(nameof(CanInfer));
            });
    }

    [RelayCommand]
    private Task InferAsync()
    {
        if (!CanInfer) { StatusMessage = "请输入源音频、参考音频和 checkpoint。"; return Task.CompletedTask; }
        return RunHost.RunAsync<SeedVcInferResult>(
            async (context, token) => await _services.Workflows.SeedVc.InferAsync(new SeedVcInferRequest(SeedVcRoot!, SourceAudioPath!, ReferenceAudioPath!, CheckpointPath!, PythonPath: PythonPath, OutputDirectory: InferOutputPath), context, token).ConfigureAwait(false),
            result =>
            {
                LastInfer = result;
                _lastInferDirectory = result.RunDirectory;
                OnPropertyChanged(nameof(LastInferDirectory));
                PersistSettings();
                StatusMessage = InferSummary;
                OnPropertyChanged(nameof(InferSummary));
            });
    }

    [RelayCommand]
    private void PlayInferenceResult()
    {
        if (LastInfer?.Status == SeedVcInferStatus.Completed && IsUsableFile(LastInfer.OutputPath))
        {
            _services.AudioPreview.Play(LastInfer.OutputPath);
            StatusMessage = "正在播放最近一次 Seed‑VC 转换结果。";
        }
    }

    [RelayCommand]
    private void StopInferencePreview() => _services.AudioPreview.Stop();

    [RelayCommand]
    private void OpenRunDirectory()
    {
        var path = LastTrain?.RunDirectory ?? _lastRunDirectory ?? _prepared?.OutputDirectory;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    partial void OnSeedVcRootChanged(string? value) { OnPropertyChanged(nameof(CanTrain)); OnPropertyChanged(nameof(CanInfer)); PersistSettings(); }
    partial void OnPythonPathChanged(string? value) => PersistSettings();
    partial void OnPrepDirectoryChanged(string? value) { OnPropertyChanged(nameof(CanTrain)); PersistSettings(); }
    partial void OnCheckpointPathChanged(string? value) { OnPropertyChanged(nameof(CanInfer)); PersistSettings(); }
    partial void OnSourceAudioPathChanged(string? value) { OnPropertyChanged(nameof(CanInfer)); PersistSettings(); }
    partial void OnReferenceAudioPathChanged(string? value) { OnPropertyChanged(nameof(CanInfer)); PersistSettings(); }
    partial void OnInferOutputPathChanged(string? value) => PersistSettings();
    partial void OnRunNameChanged(string? value) => PersistSettings();

    private static string? ReadDatasetFingerprint(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return null;
        try
        {
            var path = Path.Combine(directory, "build-manifest.json");
            return File.Exists(path) ? System.Text.Json.JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("buildFingerprint").GetString() : null;
        }
        catch { return null; }
    }

    private static string? FindReusablePreparation(string datasetDirectory)
    {
        try
        {
            var fingerprint = ReadDatasetFingerprint(datasetDirectory);
            if (string.IsNullOrWhiteSpace(fingerprint)) return null;
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WeChatVoiceToolkit", "SeedVcPrep");
            if (!Directory.Exists(root)) return null;
            foreach (var manifest in Directory.EnumerateFiles(root, "prep-manifest.json", SearchOption.AllDirectories))
            {
                try
                {
                    using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifest));
                    if (document.RootElement.TryGetProperty("datasetBuildFingerprint", out var value)
                        && string.Equals(value.GetString(), fingerprint, StringComparison.OrdinalIgnoreCase))
                    {
                        var preparation = Path.GetFullPath(Path.GetDirectoryName(Path.GetDirectoryName(manifest)!)!);
                        if (VerifyPreparationFiles(preparation, document.RootElement)) return preparation;
                    }
                }
                catch (IOException) { }
                catch (System.Text.Json.JsonException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }

    private static bool IsUsableFile(string? path)
        => !string.IsNullOrWhiteSpace(path) && File.Exists(path) && new FileInfo(path).Length > 0;

    private static SeedVcTrainResult? TryReadTrainResult(string? runDirectory)
    {
        if (string.IsNullOrWhiteSpace(runDirectory)) return null;
        try
        {
            var manifestPath = Path.Combine(runDirectory, "run-manifest.json");
            if (!File.Exists(manifestPath)) return null;
            var manifest = JsonSerializer.Deserialize<SeedVcTrainManifest>(File.ReadAllText(manifestPath));
            if (manifest is null) return null;
            return new SeedVcTrainResult(
                Path.GetFullPath(runDirectory),
                manifestPath,
                Path.Combine(runDirectory, manifest.LogRelativePath.Replace('/', Path.DirectorySeparatorChar)),
                manifest.RunId,
                manifest.Status,
                manifest.ExitCode,
                manifest.Checkpoints);
        }
        catch (IOException) { return null; }
        catch (JsonException) { return null; }
    }

    private static SeedVcInferResult? TryReadInferResult(string? runDirectory)
    {
        if (string.IsNullOrWhiteSpace(runDirectory)) return null;
        try
        {
            var manifestPath = Path.Combine(runDirectory, "infer-manifest.json");
            if (!File.Exists(manifestPath)) return null;
            var manifest = JsonSerializer.Deserialize<SeedVcInferManifest>(File.ReadAllText(manifestPath));
            if (manifest is null) return null;
            var outputPath = Path.Combine(runDirectory, manifest.OutputRelativePath.Replace('/', Path.DirectorySeparatorChar));
            return new SeedVcInferResult(
                Path.GetFullPath(runDirectory), manifestPath,
                Path.Combine(runDirectory, manifest.LogRelativePath.Replace('/', Path.DirectorySeparatorChar)),
                outputPath, manifest.RunId, manifest.Status, manifest.ExitCode,
                manifest.OutputByteLength, manifest.OutputSha256);
        }
        catch (IOException) { return null; }
        catch (JsonException) { return null; }
    }

    private static bool IsReusablePreparation(string? path, string fingerprint)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return false;
        var manifest = Path.Combine(path, "manifests", "prep-manifest.json");
        if (!File.Exists(manifest)) return false;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifest));
            return document.RootElement.TryGetProperty("datasetBuildFingerprint", out var value)
                && string.Equals(value.GetString(), fingerprint, StringComparison.OrdinalIgnoreCase)
                && VerifyPreparationFiles(path, document.RootElement);
        }
        catch (IOException) { return false; }
        catch (System.Text.Json.JsonException) { return false; }
    }

    private static bool VerifyPreparationFiles(string root, System.Text.Json.JsonElement manifest)
    {
        if (!manifest.TryGetProperty("items", out var items)) return false;
        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("state", out var state)
                || !string.Equals(state.GetString(), "kept", StringComparison.OrdinalIgnoreCase)) continue;
            if (!item.TryGetProperty("relativeAudioPath", out var relative)
                || !item.TryGetProperty("sha256", out var expected)
                || string.IsNullOrWhiteSpace(relative.GetString())
                || string.IsNullOrWhiteSpace(expected.GetString())) return false;
            var path = Path.GetFullPath(Path.Combine(root, relative.GetString()!.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return false;
            using var stream = File.OpenRead(path);
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
            if (!string.Equals(hash, expected.GetString(), StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    partial void OnAnchorDirectoryChanged(string? value)
    {
        // A newly selected anchor set changes the deterministic prep
        // fingerprint; never train accidentally on a previous anchor set.
        if (!string.IsNullOrWhiteSpace(value))
        {
            PrepDirectory = null;
            _prepared = null;
            OnPropertyChanged(nameof(PreparedSummary));
        }
        PersistSettings();
    }
}
