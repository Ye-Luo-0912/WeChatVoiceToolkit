using System.Diagnostics;
using System.Text.Json;
using WeChatVoice.Core.Models;
using WeChatVoice.Infrastructure.Audio;

namespace WeChatVoice.Infrastructure.SeedVc;

/// <summary>Read-only checks for a user-managed Seed-VC installation.</summary>
public sealed class SeedVcDoctorService
{
    public async Task<SeedVcDoctorReport> CheckAsync(SeedVcDoctorRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var issues = new List<string>();
        var root = string.IsNullOrWhiteSpace(request.SeedVcRoot) ? null : Path.GetFullPath(request.SeedVcRoot);
        if (root is null || !Directory.Exists(root))
        {
            issues.Add("seedvc-root-missing");
        }

        var python = ResolvePython(request.PythonPath);
        string? pythonVersion = null;
        string? torchVersion = null;
        bool? cuda = null;
        string? gpu = null;
        if (python is null)
        {
            issues.Add("python-missing");
        }
        else
        {
            var probe = await RunPythonProbeAsync(python, cancellationToken).ConfigureAwait(false);
            pythonVersion = probe.PythonVersion;
            torchVersion = probe.TorchVersion;
            cuda = probe.CudaAvailable;
            gpu = probe.GpuName;
            if (!probe.Success) issues.Add("python-torch-unavailable");
            if (probe.CudaAvailable != true) issues.Add("cuda-unavailable");
        }

        var config = string.IsNullOrWhiteSpace(request.ConfigPath)
            ? root is null ? null : Path.Combine(root, "configs", "presets", "config_dit_mel_seed_uvit_whisper_small_wavenet.yml")
            : Path.GetFullPath(request.ConfigPath);
        if (config is null || !File.Exists(config)) issues.Add("seedvc-config-missing");
        if (root is not null && !File.Exists(Path.Combine(root, "train.py"))) issues.Add("train-script-missing");
        var ffmpeg = FfmpegLocator.Discover();
        if (ffmpeg is null) issues.Add("ffmpeg-missing");

        return new SeedVcDoctorReport(
            issues.Count == 0,
            root,
            python,
            pythonVersion,
            torchVersion,
            cuda,
            gpu,
            config,
            ffmpeg,
            issues,
            DateTimeOffset.UtcNow);
    }

    private static string? ResolvePython(string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            var full = Path.GetFullPath(requested);
            return File.Exists(full) ? full : null;
        }

        foreach (var candidate in OperatingSystem.IsWindows() ? new[] { "python.exe", "py.exe" } : new[] { "python3", "python" })
        {
            try
            {
                var start = new ProcessStartInfo(candidate, "--version") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                using var process = Process.Start(start);
                if (process is not null) { process.WaitForExit(1500); if (process.ExitCode == 0) return candidate; }
            }
            catch (Exception) { }
        }

        return null;
    }

    private static async Task<(bool Success, string? PythonVersion, string? TorchVersion, bool? CudaAvailable, string? GpuName)> RunPythonProbeAsync(string python, CancellationToken cancellationToken)
    {
        const string script = "import json,sys; r={'python':sys.version.split()[0]};\ntry:\n import torch; r.update(torch=str(torch.__version__),cuda=bool(torch.cuda.is_available()),gpu=(torch.cuda.get_device_name(0) if torch.cuda.is_available() else None))\nexcept Exception: r.update(torch=None,cuda=False,gpu=None)\nprint(json.dumps(r))";
        var start = new ProcessStartInfo
        {
            FileName = python,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(script);
        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException();
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (process.ExitCode != 0) return (false, null, null, false, null);
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            return (true,
                root.TryGetProperty("python", out var py) ? py.GetString() : null,
                root.TryGetProperty("torch", out var torch) && torch.ValueKind != JsonValueKind.Null ? torch.GetString() : null,
                root.TryGetProperty("cuda", out var cuda) && cuda.ValueKind != JsonValueKind.Null && cuda.GetBoolean(),
                root.TryGetProperty("gpu", out var gpu) && gpu.ValueKind != JsonValueKind.Null ? gpu.GetString() : null);
        }
        catch (Exception)
        {
            return (false, null, null, false, null);
        }
    }
}
