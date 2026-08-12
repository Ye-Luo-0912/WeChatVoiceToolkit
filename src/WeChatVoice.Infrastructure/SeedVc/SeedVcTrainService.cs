using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WeChatVoice.Core.Errors;
using WeChatVoice.Core.Models;

namespace WeChatVoice.Infrastructure.SeedVc;

/// <summary>
/// Runs the upstream Seed-VC training script with an explicit argv list. It
/// never invokes a shell and records enough provenance to resume a run safely.
/// </summary>
public sealed class SeedVcTrainService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public async Task<SeedVcTrainResult> TrainAsync(
        SeedVcTrainRequest request,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var prepRoot = Path.GetFullPath(request.PrepDirectory);
        var seedRoot = Path.GetFullPath(request.SeedVcRoot);
        if (!Directory.Exists(prepRoot) || !File.Exists(Path.Combine(prepRoot, "manifests", "prep-manifest.json")))
        {
            throw new AppFailureException(ErrorCode.InvalidRequest, "The Seed-VC preparation directory is missing or incomplete.");
        }
        if (!Directory.Exists(seedRoot) || !File.Exists(Path.Combine(seedRoot, "train.py")))
        {
            throw new AppFailureException(ErrorCode.InvalidRequest, "The Seed-VC root must contain train.py.");
        }

        var prepManifest = await ReadAsync<SeedVcPrepareManifest>(Path.Combine(prepRoot, "manifests", "prep-manifest.json"), cancellationToken).ConfigureAwait(false);
        if (prepManifest.KeptCount == 0) throw new AppFailureException(ErrorCode.InvalidRequest, "The prepared Seed-VC dataset contains no usable audio.");
        var config = ResolveConfig(seedRoot, request.ConfigPath);
        if (!File.Exists(config)) throw new AppFailureException(ErrorCode.InvalidRequest, "The selected Seed-VC training config does not exist.");
        if (request.BatchSize is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(request.BatchSize));
        if (request.MaxSteps is < 1 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(request.MaxSteps));
        if (request.MaxEpochs is < 1 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(request.MaxEpochs));
        if (request.SaveEvery is < 1 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(request.SaveEvery));

        var runName = SanitizeRunName(request.RunName ?? $"seedvc-{prepManifest.PrepFingerprint[..12]}");
        var outputRoot = Path.GetFullPath(request.OutputDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WeChatVoiceToolkit", "SeedVcRuns", runName));
        if (PathOverlap(outputRoot, prepRoot)) throw new AppFailureException(ErrorCode.InvalidRequest, "The training output must not be inside the prepared dataset.");
        Directory.CreateDirectory(outputRoot);
        var logPath = Path.Combine(outputRoot, "train.log");
        var manifestPath = Path.Combine(outputRoot, "run-manifest.json");
        var runId = Guid.NewGuid().ToString("N");
        var configHash = await ComputeSha256Async(config, cancellationToken).ConfigureAwait(false);
        var relativeConfig = Path.GetRelativePath(seedRoot, config).Replace(Path.DirectorySeparatorChar, '/');
        var arguments = BuildArguments(seedRoot, prepRoot, config, runName, request);
        var started = DateTimeOffset.UtcNow;
        var initial = new SeedVcTrainManifest(
            runId,
            runName,
            prepManifest.PrepFingerprint,
            configHash,
            relativeConfig,
            null,
            null,
            null,
            string.IsNullOrWhiteSpace(request.PythonPath) ? "python" : request.PythonPath!,
            "train.py",
            arguments,
            started,
            null,
            SeedVcTrainStatus.Running,
            null,
            "train.log",
            Array.Empty<SeedVcCheckpoint>());
        await WriteJsonAsync(manifestPath, initial, cancellationToken).ConfigureAwait(false);

        var python = string.IsNullOrWhiteSpace(request.PythonPath) ? "python" : Path.GetFullPath(request.PythonPath);
        var startInfo = new ProcessStartInfo
        {
            FileName = python,
            WorkingDirectory = seedRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("train.py");
        foreach (var argument in arguments.Skip(1)) startInfo.ArgumentList.Add(argument);

        var status = SeedVcTrainStatus.Failed;
        int? exitCode = null;
        try
        {
            progress?.Report(new OperationProgress(OperationPhase.VoiceExport, OperationStatus.Running, new OperationStage(OperationStageIds.Exporting, "启动 Seed-VC 微调")));
            using var process = Process.Start(startInfo) ?? throw new AppFailureException(ErrorCode.WorkflowFailed, "Python could not be started.");
            await using var log = new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var writer = new StreamWriter(log, new UTF8Encoding(false));
            var stdout = PumpAsync(process.StandardOutput, writer, "[stdout] ", cancellationToken);
            var stderr = PumpAsync(process.StandardError, writer, "[stderr] ", cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                status = SeedVcTrainStatus.Cancelled;
                throw;
            }
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            exitCode = process.ExitCode;
            status = exitCode == 0 ? SeedVcTrainStatus.Completed : SeedVcTrainStatus.Failed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            status = SeedVcTrainStatus.Cancelled;
            throw;
        }
        finally
        {
            var checkpoints = await FindCheckpointsAsync(outputRoot, CancellationToken.None).ConfigureAwait(false);
            var final = initial with { FinishedAtUtc = status == SeedVcTrainStatus.Running ? null : DateTimeOffset.UtcNow, Status = status, ExitCode = exitCode, Checkpoints = checkpoints };
            await WriteJsonAsync(manifestPath, final, CancellationToken.None).ConfigureAwait(false);
        }

        var completed = await ReadAsync<SeedVcTrainManifest>(manifestPath, CancellationToken.None).ConfigureAwait(false);
        return new SeedVcTrainResult(outputRoot, manifestPath, logPath, runId, completed.Status, completed.ExitCode, completed.Checkpoints);
    }

    private static string[] BuildArguments(string seedRoot, string prepRoot, string config, string runName, SeedVcTrainRequest request)
        => [
            "train.py",
            "--config", Path.GetRelativePath(seedRoot, config),
            "--dataset-dir", prepRoot,
            "--run-name", runName,
            "--batch-size", request.BatchSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--max-steps", request.MaxSteps.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--max-epochs", request.MaxEpochs.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--save-every", request.SaveEvery.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--num-workers", "0",
        ];

    private static async Task PumpAsync(StreamReader reader, StreamWriter writer, string prefix, CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            await writer.WriteLineAsync((prefix + line).AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<IReadOnlyList<SeedVcCheckpoint>> FindCheckpointsAsync(string outputRoot, CancellationToken cancellationToken)
    {
        var list = new List<SeedVcCheckpoint>();
        if (!Directory.Exists(outputRoot)) return list;
        foreach (var path in Directory.EnumerateFiles(outputRoot, "*", SearchOption.AllDirectories)
                     .Where(static path => path.EndsWith(".pth", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".pt", StringComparison.OrdinalIgnoreCase)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            list.Add(new SeedVcCheckpoint(Path.GetRelativePath(outputRoot, path).Replace(Path.DirectorySeparatorChar, '/'), info.Length, await ComputeSha256Async(path, cancellationToken).ConfigureAwait(false)));
        }
        return list.OrderBy(static checkpoint => checkpoint.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string ResolveConfig(string seedRoot, string? requested)
        => string.IsNullOrWhiteSpace(requested)
            ? Path.Combine(seedRoot, "configs", "presets", "config_dit_mel_seed_uvit_whisper_small_wavenet.yml")
            : Path.GetFullPath(requested);

    private static string SanitizeRunName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Trim().Select(character => invalid.Contains(character) ? '-' : character).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "seedvc-run" : sanitized[..Math.Min(80, sanitized.Length)];
    }

    private static bool PathOverlap(string left, string right)
    {
        var a = Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var b = Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return a.StartsWith(b, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0) hash.AppendData(buffer, 0, read);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static async Task<T> ReadAsync<T>(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false) ?? throw new InvalidDataException("The JSON document is empty.");
    }

    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } }
    }
}
