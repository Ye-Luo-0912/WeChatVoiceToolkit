using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WeChatVoice.Core.Errors;
using WeChatVoice.Core.Models;

namespace WeChatVoice.Infrastructure.SeedVc;

/// <summary>
/// Synchronizes a verified preparation directory to the configured Linux host
/// and starts the upstream training script with fixed arguments. The sync is
/// content-addressed and resumable; no shell command is accepted from callers.
/// </summary>
public sealed class SeedVcRemoteTrainService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromHours(24);

    public async Task<SeedVcRemoteTrainResult> TrainAsync(
        SeedVcRemoteTrainRequest request,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var prepRoot = Path.GetFullPath(request.PrepDirectory);
        var prepManifestPath = Path.Combine(prepRoot, "manifests", "prep-manifest.json");
        if (!Directory.Exists(prepRoot) || !File.Exists(prepManifestPath))
        {
            throw new AppFailureException(ErrorCode.InvalidRequest, "The Seed-VC preparation directory is missing or incomplete.");
        }

        var prepManifest = await ReadAsync<SeedVcPrepareManifest>(prepManifestPath, cancellationToken).ConfigureAwait(false);
        if (prepManifest.KeptCount == 0) throw new AppFailureException(ErrorCode.InvalidRequest, "The prepared Seed-VC dataset contains no usable audio.");
        var resolution = new SeedVcToolchainResolver().Resolve();
        if (string.IsNullOrWhiteSpace(resolution.LinuxHost)) throw new AppFailureException(ErrorCode.InvalidRequest, "Linux SSH host is not configured. Run 'seedvc config set --linux-host <alias>'.");
        if (string.IsNullOrWhiteSpace(resolution.LinuxSeedVcRoot)) throw new AppFailureException(ErrorCode.InvalidRequest, "Linux Seed-VC root is not configured.");

        // Fail before uploading a large preparation when the fixed remote
        // toolchain cannot actually start training. In particular, a Python
        // environment can be healthy while its model cache is incomplete.
        var remoteProbe = await new SeedVcRemoteProbeService().ProbeAsync(resolution, cancellationToken).ConfigureAwait(false);
        if (!remoteProbe.IsReady)
        {
            var issueText = remoteProbe.Issues.Count == 0 ? "remote-toolchain-not-ready" : string.Join(",", remoteProbe.Issues);
            throw new AppFailureException(ErrorCode.InvalidRequest, $"Remote Seed-VC is not ready: {issueText}. Run 'seedvc remote doctor' for details.");
        }

        ValidateTrainOptions(request);
        var localRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WeChatVoiceToolkit", "SeedVcRemoteRuns", request.RunName ?? $"seedvc-{prepManifest.PrepFingerprint[..12]}");
        localRoot = Path.GetFullPath(localRoot);
        Directory.CreateDirectory(localRoot);
        var localManifestPath = Path.Combine(localRoot, "remote-run-manifest.json");
        var localLogPath = Path.Combine(localRoot, "remote-train.log");
        var remoteBase = "/tmp/wechatvoice-seedvc";
        var remotePrep = remoteBase + "/prep/" + prepManifest.PrepFingerprint;
        var runName = SanitizeRunName(request.RunName ?? $"seedvc-{prepManifest.PrepFingerprint[..12]}");
        var remoteRun = resolution.LinuxSeedVcRoot!.TrimEnd('/') + "/runs/" + runName;

        progress?.Report(new OperationProgress(OperationPhase.SeedVc, OperationStatus.Running, new OperationStage(OperationStageIds.TrainingSeedVc, "同步 Seed-VC 训练数据")));
        var dataReused = await SyncPreparationAsync(prepRoot, remotePrep, resolution, cancellationToken).ConfigureAwait(false);
        var command = BuildRemoteTrainCommand(resolution.LinuxSeedVcRoot!, resolution.LinuxPythonPath, resolution.LinuxFfmpegPath, remotePrep, remoteRun, request);
        var output = await RunSshAsync(resolution, command, localLogPath, progress, cancellationToken).ConfigureAwait(false);
        var remoteCheckpoints = ParseCheckpoints(output);
        var checkpoints = await RetrieveCheckpointsAsync(remoteCheckpoints, resolution, localRoot, cancellationToken).ConfigureAwait(false);
        var status = output.ExitCode == 0 ? SeedVcTrainStatus.Completed : SeedVcTrainStatus.Failed;
        var runId = prepManifest.PrepFingerprint[..Math.Min(16, prepManifest.PrepFingerprint.Length)] + "-remote";
        var result = new SeedVcRemoteTrainResult(localRoot, localManifestPath, localLogPath, remotePrep, remoteRun, runId, status, output.ExitCode, checkpoints.LastOrDefault()?.RelativePath, checkpoints, dataReused, output.ExitCode == 0 ? Array.Empty<string>() : ["remote-train-failed"]);
        await WriteJsonAsync(localManifestPath, result, CancellationToken.None).ConfigureAwait(false);
        return result;
    }

    internal static string BuildRemoteTrainCommand(string seedRoot, string? python, string? ffmpeg, string prepRoot, string runRoot, SeedVcRemoteTrainRequest request)
    {
        var py = Quote(string.IsNullOrWhiteSpace(python) ? "python3" : python!);
        var root = Quote(seedRoot);
        var prep = Quote(prepRoot);
        var run = Quote(runRoot);
        var config = Quote(seedRoot.TrimEnd('/') + "/configs/presets/config_dit_mel_seed_uvit_whisper_small_wavenet.yml");
        var name = Quote(SanitizeRunName(request.RunName ?? "seedvc-run"));
        var train = $"mkdir -p {run}; cd {root}; if [ ! -e checkpoints/hf_cache/models--openai--whisper-small ]; then cp -as \"$HOME/.cache/huggingface/hub/models--openai--whisper-small\" checkpoints/hf_cache/ 2>/dev/null || true; fi; checkpoint=$(find -L checkpoints/models--Plachta--Seed-VC/snapshots -type f -name DiT_seed_v2_uvit_whisper_small_wavenet_bigvgan_pruned.pth -size +1c -print -quit 2>/dev/null); test -n \"$checkpoint\" || {{ echo 'Seed-VC pretrained checkpoint is missing' >&2; exit 78; }}; {py} train.py --config {config} --pretrained-ckpt \"$checkpoint\" --dataset-dir {prep} --run-name {name} --batch-size {request.BatchSize} --max-steps {request.MaxSteps} --max-epochs {request.MaxEpochs} --save-every {request.SaveEvery} --num-workers 0 --gpu 0; status=$?; find {run} -type f \\( -name '*.pth' -o -name '*.pt' \\) -printf 'checkpoint=%p\\t%s\\n'; exit $status";
        return $"sh -c {Quote(train)}";
    }

    private static async Task<bool> SyncPreparationAsync(string localRoot, string remoteRoot, SeedVcToolchainResolution resolution, CancellationToken cancellationToken)
    {
        var files = Directory.EnumerateFiles(localRoot, "*", SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + ".staging-", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(path => new { Path = path, Relative = Path.GetRelativePath(localRoot, path).Replace(Path.DirectorySeparatorChar, '/') })
            .ToArray();
        // Keep a conventional sha256sum -c manifest as well as the compact
        // content-address marker.  The latter avoids a transfer; the former
        // proves that a reused remote directory still contains the exact
        // bytes, rather than merely trusting a stale marker file.
        var manifest = string.Join('\n', files.Select(file => file.Relative + "\t" + ComputeSha256(file.Path)));
        var checksumManifest = string.Join('\n', files.Select(file => ComputeSha256(file.Path) + "  " + file.Relative)) + "\n";
        var manifestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifest))).ToLowerInvariant();
        var command = $"test -f {Quote(remoteRoot + "/.complete")} && test \"$(cat {Quote(remoteRoot + "/.manifest.sha256")})\" = {Quote(manifestHash)} && test -f {Quote(remoteRoot + "/.manifest.files")} && (cd {Quote(remoteRoot)} && sha256sum -c .manifest.files >/dev/null)";
        var probe = await RunSshAsync(resolution, command, null, null, cancellationToken).ConfigureAwait(false);
        if (probe.ExitCode == 0) return true;

        var create = await RunSshAsync(resolution, $"mkdir -p {Quote(remoteRoot + "/audio")} {Quote(remoteRoot + "/manifests")}", null, null, cancellationToken).ConfigureAwait(false);
        if (create.ExitCode != 0) throw new AppFailureException(ErrorCode.WorkflowFailed, "Remote preparation directory could not be created.");

        // SCP is intentionally invoked with explicit arguments; it transfers
        // only verified preparation files and never accepts a remote command.
        foreach (var file in files)
        {
            await RunScpAsync(resolution, file.Path, remoteRoot + "/" + file.Relative, cancellationToken).ConfigureAwait(false);
        }
        var localManifestFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(localManifestFile, checksumManifest, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            await RunScpAsync(resolution, localManifestFile, remoteRoot + "/.manifest.files", cancellationToken).ConfigureAwait(false);
            var complete = await RunSshAsync(resolution, $"printf '%s' {Quote(manifestHash)} > {Quote(remoteRoot + "/.manifest.sha256")}; (cd {Quote(remoteRoot)} && sha256sum -c .manifest.files >/dev/null) && touch {Quote(remoteRoot + "/.complete")}", null, null, cancellationToken).ConfigureAwait(false);
            if (complete.ExitCode != 0) throw new AppFailureException(ErrorCode.WorkflowFailed, "Remote preparation synchronization failed.");
            return false;
        }
        finally
        {
            try { File.Delete(localManifestFile); } catch { }
        }
    }

    private static async Task<ProcessResult> RunScpAsync(SeedVcToolchainResolution resolution, string localPath, string remotePath, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = CreateStartInfo("scp", resolution) };
        process.StartInfo.ArgumentList.Add(localPath);
        process.StartInfo.ArgumentList.Add(Target(resolution) + ":" + remotePath);
        if (!process.Start()) throw new AppFailureException(ErrorCode.WorkflowFailed, "SCP could not be started.");
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0) throw new AppFailureException(ErrorCode.WorkflowFailed, "Remote preparation synchronization failed.");
        return new ProcessResult(process.ExitCode, string.Empty);
    }

    private static async Task<ProcessResult> RunSshAsync(SeedVcToolchainResolution resolution, string command, string? logPath, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = CreateStartInfo("ssh", resolution) };
        process.StartInfo.ArgumentList.Add(Target(resolution));
        process.StartInfo.ArgumentList.Add(command);
        if (!process.Start()) throw new AppFailureException(ErrorCode.WorkflowFailed, "SSH could not be started.");
        await using var log = logPath is null ? null : new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
        using var writer = log is null ? null : new StreamWriter(log, new UTF8Encoding(false));
        var output = new StringBuilder();
        var stdout = PumpAsync(process.StandardOutput, output, writer, cancellationToken);
        var stderr = PumpAsync(process.StandardError, output, writer, cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProcessTimeout);
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { try { if (!process.HasExited) process.Kill(true); } catch { } throw; }
        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        if (writer is not null) await writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        progress?.Report(new OperationProgress(OperationPhase.SeedVc, OperationStatus.Running, new OperationStage(OperationStageIds.TrainingSeedVc, "远端 Seed-VC 训练运行中")));
        return new ProcessResult(process.ExitCode, output.ToString());
    }

    private static ProcessStartInfo CreateStartInfo(string fileName, SeedVcToolchainResolution resolution)
    {
        var info = new ProcessStartInfo { FileName = fileName, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        info.ArgumentList.Add("-o"); info.ArgumentList.Add("BatchMode=yes");
        info.ArgumentList.Add("-o"); info.ArgumentList.Add("ConnectTimeout=8");
        if (resolution.LinuxPort is > 0)
        {
            info.ArgumentList.Add(fileName.Equals("scp", StringComparison.OrdinalIgnoreCase) ? "-P" : "-p");
            info.ArgumentList.Add(resolution.LinuxPort.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        return info;
    }

    private static string Target(SeedVcToolchainResolution resolution) => string.IsNullOrWhiteSpace(resolution.LinuxUser) ? resolution.LinuxHost! : resolution.LinuxUser + "@" + resolution.LinuxHost;
    private static string Quote(string value) => $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";
    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string SanitizeRunName(string value)
    {
        var sanitized = new string(value.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "seedvc-run" : sanitized[..Math.Min(80, sanitized.Length)];
    }
    private static void ValidateTrainOptions(SeedVcRemoteTrainRequest request) { if (request.BatchSize is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(request.BatchSize)); if (request.MaxSteps is < 1 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(request.MaxSteps)); if (request.MaxEpochs is < 1 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(request.MaxEpochs)); if (request.SaveEvery is < 1 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(request.SaveEvery)); }
    private static async Task PumpAsync(StreamReader reader, StringBuilder output, StreamWriter? writer, CancellationToken token)
    {
        while (await reader.ReadLineAsync(token).ConfigureAwait(false) is { } line)
        {
            if (output.Length < 256 * 1024) output.AppendLine(line.Length > 2048 ? line[..2048] : line);
            if (writer is not null) await writer.WriteLineAsync(line.AsMemory(), token).ConfigureAwait(false);
        }
    }

    private static IReadOnlyList<RemoteCheckpoint> ParseCheckpoints(ProcessResult output)
        => output.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(static line => line.StartsWith("checkpoint=", StringComparison.Ordinal))
            .Select(line => line["checkpoint=".Length..].Split('\t', 2))
            .Where(parts => parts.Length == 2 && long.TryParse(parts[1], out _))
            .Select(parts => new RemoteCheckpoint(parts[0], long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture)))
            .DistinctBy(static checkpoint => checkpoint.Path, StringComparer.Ordinal)
            .ToArray();

    private static async Task<IReadOnlyList<SeedVcCheckpoint>> RetrieveCheckpointsAsync(
        IReadOnlyList<RemoteCheckpoint> remoteCheckpoints,
        SeedVcToolchainResolution resolution,
        string localRoot,
        CancellationToken cancellationToken)
    {
        if (remoteCheckpoints.Count == 0) return Array.Empty<SeedVcCheckpoint>();
        var selected = remoteCheckpoints[^1];
        var directory = Path.Combine(localRoot, "checkpoints");
        Directory.CreateDirectory(directory);
        var localPath = Path.Combine(directory, Path.GetFileName(selected.Path));
        await RunScpToLocalAsync(resolution, selected.Path, localPath, cancellationToken).ConfigureAwait(false);
        var metadata = new FileInfo(localPath);
        if (metadata.Length != selected.Length) throw new AppFailureException(ErrorCode.WorkflowFailed, "The retrieved remote checkpoint length does not match the remote report.");
        return [new SeedVcCheckpoint(Path.GetRelativePath(localRoot, localPath).Replace(Path.DirectorySeparatorChar, '/'), metadata.Length, ComputeSha256(localPath))];
    }

    private static async Task RunScpToLocalAsync(SeedVcToolchainResolution resolution, string remotePath, string localPath, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = CreateStartInfo("scp", resolution) };
        process.StartInfo.ArgumentList.Add(Target(resolution) + ":" + remotePath);
        process.StartInfo.ArgumentList.Add(localPath);
        if (!process.Start()) throw new AppFailureException(ErrorCode.WorkflowFailed, "SCP could not be started.");
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0) throw new AppFailureException(ErrorCode.WorkflowFailed, "Remote checkpoint retrieval failed.");
    }
    private static async Task<T> ReadAsync<T>(string path, CancellationToken token) { await using var stream = File.OpenRead(path); return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, token).ConfigureAwait(false) ?? throw new InvalidDataException("The JSON document is empty."); }
    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken token) { await using var stream = File.Create(path); await JsonSerializer.SerializeAsync(stream, value, JsonOptions, token).ConfigureAwait(false); }
    private sealed record ProcessResult(int ExitCode, string Output);
    private sealed record RemoteCheckpoint(string Path, long Length);
}
