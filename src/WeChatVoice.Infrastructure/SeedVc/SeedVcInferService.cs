using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WeChatVoice.Core.Errors;
using WeChatVoice.Core.Models;
using WeChatVoice.Infrastructure.Export;
using WeChatVoice.Infrastructure.Serialization;

namespace WeChatVoice.Infrastructure.SeedVc;

/// <summary>
/// Runs the small repository-owned Seed-VC inference bridge. The bridge is
/// invoked with an explicit argv list; no shell, command interpolation, or
/// hidden network operation is involved. The bridge is intentionally separate
/// from the upstream Gradio app so Desktop and CLI get a deterministic WAV.
/// </summary>
public sealed class SeedVcInferService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public async Task<SeedVcInferResult> InferAsync(
        SeedVcInferRequest request,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.DiffusionSteps is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(request.DiffusionSteps));
        if (request.LengthAdjust is < 0.5 or > 2) throw new ArgumentOutOfRangeException(nameof(request.LengthAdjust));
        if (request.InferenceCfgRate is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(request.InferenceCfgRate));

        var toolchain = new SeedVcToolchainResolver().Resolve(request.SeedVcRoot, request.PythonPath, request.ConfigPath);
        var root = string.IsNullOrWhiteSpace(toolchain.SeedVcRoot)
            ? throw new AppFailureException(ErrorCode.InvalidRequest, "Seed-VC root is not configured. Run 'seedvc config set --seedvc-root <path>' or pass --seedvc-root.")
            : Path.GetFullPath(toolchain.SeedVcRoot);
        var source = RequireFile(request.SourceAudioPath, "source audio");
        var reference = RequireFile(request.ReferenceAudioPath, "reference audio");
        var checkpoint = RequireFile(request.CheckpointPath, "checkpoint");
        var config = string.IsNullOrWhiteSpace(toolchain.ConfigPath)
            ? Path.Combine(root, "configs", "presets", "config_dit_mel_seed_uvit_whisper_small_wavenet.yml")
            : Path.GetFullPath(toolchain.ConfigPath!);
        config = RequireFile(config, "config");
        var script = File.Exists(Path.Combine(root, "seedvc_infer.py"))
            ? Path.Combine(root, "seedvc_infer.py")
            : Path.Combine(AppContext.BaseDirectory, "seedvc_infer.py");
        if (!File.Exists(script))
        {
            throw new AppFailureException(ErrorCode.InvalidRequest, "Seed-VC inference bridge is not installed. Rebuild or publish the application again.");
        }

        var runName = Sanitize(request.RunName ?? $"infer-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}");
        var runRoot = Path.GetFullPath(request.OutputDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WeChatVoiceToolkit", "SeedVcRuns", runName));
        if (PathOverlap(runRoot, source) || PathOverlap(runRoot, reference) || PathOverlap(runRoot, checkpoint))
        {
            throw new AppFailureException(ErrorCode.InvalidRequest, "Inference output cannot contain an input file.");
        }
        Directory.CreateDirectory(runRoot);
        var output = Path.Combine(runRoot, "converted.wav");
        var manifestPath = Path.Combine(runRoot, "infer-manifest.json");
        var logPath = Path.Combine(runRoot, "infer.log");
        var previous = await ReadExistingManifestAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        var runId = previous?.RunId ?? Guid.NewGuid().ToString("N");
        var args = BuildArguments(script, checkpoint, config, source, reference, output, request);
        var sourceHash = await FileHashing.ComputeSha256Async(source, cancellationToken).ConfigureAwait(false);
        var referenceHash = await FileHashing.ComputeSha256Async(reference, cancellationToken).ConfigureAwait(false);
        var checkpointHash = await FileHashing.ComputeSha256Async(checkpoint, cancellationToken).ConfigureAwait(false);
        var configHash = await FileHashing.ComputeSha256Async(config, cancellationToken).ConfigureAwait(false);
        if (previous is not null)
        {
            if (!string.Equals(previous.SourceSha256, sourceHash, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(previous.ReferenceSha256, referenceHash, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(previous.CheckpointSha256, checkpointHash, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(previous.ConfigSha256, configHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new AppFailureException(ErrorCode.InvalidRequest, "The existing inference run uses different input audio, checkpoint, or config. Choose a new run name.");
            }

            if (previous.Status == SeedVcInferStatus.Completed
                && previous.OutputSha256 is not null
                && File.Exists(output)
                && await FileHashing.ComputeSha256Async(output, cancellationToken).ConfigureAwait(false) == previous.OutputSha256
                && await WavFileValidator.IsValidAsync(output, cancellationToken).ConfigureAwait(false))
            {
                return new SeedVcInferResult(runRoot, manifestPath, logPath, output, previous.RunId, previous.Status, previous.ExitCode, previous.OutputByteLength, previous.OutputSha256);
            }
        }
        var initial = new SeedVcInferManifest(runId, runName,
            sourceHash,
            referenceHash,
            checkpointHash,
            configHash,
            toolchain.PythonPath, Path.GetFileName(script), args,
            previous?.StartedAtUtc ?? DateTimeOffset.UtcNow, null, SeedVcInferStatus.Failed, null, "converted.wav", null, null, "infer.log");
        await WriteJsonAsync(manifestPath, initial, cancellationToken).ConfigureAwait(false);

        var python = ResolvePython(toolchain.PythonPath);
        var startInfo = new ProcessStartInfo { FileName = python, WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in args) startInfo.ArgumentList.Add(arg);
        var status = SeedVcInferStatus.Failed;
        int? exitCode = null;
        try
        {
            progress?.Report(new OperationProgress(OperationPhase.SeedVc, OperationStatus.Running, new OperationStage(OperationStageIds.InferringSeedVc, "运行 Seed-VC 音色转换")));
            using var process = Process.Start(startInfo) ?? throw new AppFailureException(ErrorCode.WorkflowFailed, "Python could not be started.");
            await using var log = new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var writer = new StreamWriter(log, new UTF8Encoding(false));
            var stdout = PumpAsync(process.StandardOutput, writer, "[stdout] ", cancellationToken);
            var stderr = PumpAsync(process.StandardError, writer, "[stderr] ", cancellationToken);
            try { await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } status = SeedVcInferStatus.Cancelled; throw; }
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            exitCode = process.ExitCode;
            status = exitCode == 0 ? SeedVcInferStatus.Completed : SeedVcInferStatus.Failed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { status = SeedVcInferStatus.Cancelled; throw; }
        finally
        {
            long? length = null; string? hash = null;
            if (status == SeedVcInferStatus.Completed && File.Exists(output) && await WavFileValidator.IsValidAsync(output, CancellationToken.None).ConfigureAwait(false))
            {
                var metadata = await FileHashing.ComputeMetadataAsync(output, CancellationToken.None).ConfigureAwait(false); length = metadata.ByteLength; hash = metadata.Sha256;
            }
            else if (status == SeedVcInferStatus.Completed) status = SeedVcInferStatus.Failed;
            await WriteJsonAsync(manifestPath, initial with { FinishedAtUtc = DateTimeOffset.UtcNow, Status = status, ExitCode = exitCode, OutputByteLength = length, OutputSha256 = hash }, CancellationToken.None).ConfigureAwait(false);
        }

        var final = await ReadAsync<SeedVcInferManifest>(manifestPath, CancellationToken.None).ConfigureAwait(false);
        return new SeedVcInferResult(runRoot, manifestPath, logPath, output, runId, final.Status, final.ExitCode, final.OutputByteLength, final.OutputSha256);
    }

    private static string[] BuildArguments(string script, string checkpoint, string? config, string source, string reference, string output, SeedVcInferRequest request)
    {
        var args = new List<string> { script, "--checkpoint", checkpoint };
        if (config is not null) { args.Add("--config"); args.Add(config); }
        args.AddRange(["--source", source, "--reference", reference, "--output", output, "--diffusion-steps", request.DiffusionSteps.ToString(System.Globalization.CultureInfo.InvariantCulture), "--length-adjust", request.LengthAdjust.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), "--inference-cfg-rate", request.InferenceCfgRate.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), "--fp16", request.Fp16 ? "true" : "false"]);
        return args.ToArray();
    }

    private static async Task PumpAsync(StreamReader reader, StreamWriter writer, string prefix, CancellationToken cancellationToken)
    { while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line) { await writer.WriteLineAsync((prefix + line).AsMemory(), cancellationToken).ConfigureAwait(false); await writer.FlushAsync(cancellationToken).ConfigureAwait(false); } }

    private static string RequireFile(string path, string label)
    { var full = Path.GetFullPath(path); if (!File.Exists(full) || new FileInfo(full).Length == 0) throw new AppFailureException(ErrorCode.InvalidRequest, $"The {label} file does not exist or is empty."); return full; }
    private static bool PathOverlap(string left, string right)
    {
        if (File.Exists(right)) right = Path.GetDirectoryName(Path.GetFullPath(right))!;
        if (File.Exists(left)) left = Path.GetDirectoryName(Path.GetFullPath(left))!;
        var a = Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var b = Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return a.StartsWith(b, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolvePython(string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested)) return "python";
        return Path.IsPathRooted(requested) || requested.Contains(Path.DirectorySeparatorChar) || requested.Contains(Path.AltDirectorySeparatorChar)
            ? Path.GetFullPath(requested)
            : requested;
    }

    private static async Task<SeedVcInferManifest?> ReadExistingManifestAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return await ReadAsync<SeedVcInferManifest>(path, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new AppFailureException(ErrorCode.InvalidRequest, "The existing Seed-VC inference manifest is invalid; choose a new run name.", ex);
        }
    }

    private static string Sanitize(string value)
    {
        var sanitized = new string(value.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "seedvc-infer" : sanitized[..Math.Min(80, sanitized.Length)];
    }
    private static async Task<T> ReadAsync<T>(string path, CancellationToken token) { await using var stream = File.OpenRead(path); return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, token).ConfigureAwait(false) ?? throw new InvalidDataException("The JSON document is empty."); }
    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken token) { var temp = path + ".tmp-" + Guid.NewGuid().ToString("N"); await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous)) { await JsonSerializer.SerializeAsync(stream, value, JsonOptions, token).ConfigureAwait(false); await stream.FlushAsync(token).ConfigureAwait(false); } File.Move(temp, path, true); }
}
