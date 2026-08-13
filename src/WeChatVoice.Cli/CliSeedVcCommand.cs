using System.CommandLine;
using WeChatVoice.Core.Models;
using WeChatVoice.Infrastructure.SeedVc;
using WeChatVoice.Workflows.Workflows;

namespace WeChatVoice.Cli;

internal static partial class CliApplication
{
    static Command CreateSeedVcCommand()
    {
        var command = new Command("seedvc", "Prepare a verified dataset and run Seed-VC fine-tuning.");

        var doctor = new Command("doctor", "Check Python, torch/CUDA, Seed-VC checkout, and FFmpeg.");
        var root = new Option<string?>("--seedvc-root") { Description = "Seed-VC checkout directory." };
        var python = new Option<string?>("--python") { Description = "Optional Python executable path." };
        var config = new Option<string?>("--config") { Description = "Optional Seed-VC training config." };
        var ffmpeg = new Option<string?>("--ffmpeg") { Description = "Optional FFmpeg executable path or PATH command." };
        doctor.Options.Add(root); doctor.Options.Add(python); doctor.Options.Add(config); doctor.Options.Add(ffmpeg);
        doctor.SetAction(async (parseResult, cancellationToken) =>
        {
            try
            {
                await using var workflowRoot = CreateRoot();
                var result = await workflowRoot.SeedVc.DoctorAsync(
                    new SeedVcDoctorRequest(parseResult.GetValue(root), parseResult.GetValue(python), parseResult.GetValue(config), parseResult.GetValue(ffmpeg)),
                    new WorkflowContext(workflowRoot.AccountConfirmation, new Progress<OperationProgress>(ReportProgress)),
                    cancellationToken).ConfigureAwait(false);
                WriteJson(result);
                return result.IsReady ? 0 : 1;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return 130; }
            catch (Exception exception) { WriteError(exception); return 1; }
        });

        var remote = new Command("remote", "Read-only checks for the global Linux Seed-VC host.");
        var remoteDoctor = new Command("doctor", "Verify SSH connectivity, Python, FFmpeg, and Seed-VC files without uploading data.");
        remoteDoctor.SetAction(async (_, cancellationToken) =>
        {
            try
            {
                await using var workflowRoot = CreateRoot();
                var result = await workflowRoot.SeedVc.RemoteDoctorAsync(
                    new WorkflowContext(workflowRoot.AccountConfirmation, new Progress<OperationProgress>(ReportProgress)),
                    cancellationToken).ConfigureAwait(false);
                WriteJson(result);
                return result.IsReady ? 0 : 1;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return 130; }
            catch (Exception exception) { WriteError(exception); return 1; }
        });
        remote.Subcommands.Add(remoteDoctor);

        var prepare = new Command("prepare", "Create a reproducible 1-30 second Seed-VC audio set.");
        var dataset = new Option<string>("--dataset") { Required = true, Description = "Verified Dataset build directory." };
        var anchor = new Option<string?>("--anchor") { Description = "Optional phone recording directory." };
        var output = new Option<string?>("--out") { Description = "Optional preparation output directory." };
        var min = new Option<double>("--min-seconds") { DefaultValueFactory = _ => 1, Description = "Minimum clip length." };
        var max = new Option<double>("--max-seconds") { DefaultValueFactory = _ => 30, Description = "Maximum clip length." };
        var chunk = new Option<double>("--chunk-seconds") { DefaultValueFactory = _ => 10, Description = "Target segment length for long clips." };
        var weight = new Option<int>("--anchor-weight") { DefaultValueFactory = _ => 2, Description = "Copies per phone anchor." };
        prepare.Options.Add(dataset); prepare.Options.Add(anchor); prepare.Options.Add(output); prepare.Options.Add(min); prepare.Options.Add(max); prepare.Options.Add(chunk); prepare.Options.Add(weight);
        prepare.SetAction(async (parseResult, cancellationToken) =>
        {
            try
            {
                await using var workflowRoot = CreateRoot();
                var profile = new SeedVcPrepareProfile(parseResult.GetValue(min), parseResult.GetValue(max), parseResult.GetValue(chunk), parseResult.GetValue(weight));
                var result = await workflowRoot.SeedVc.PrepareAsync(
                    new SeedVcPrepareRequest(parseResult.GetValue(dataset)!, parseResult.GetValue(anchor), parseResult.GetValue(output), profile),
                    new WorkflowContext(workflowRoot.AccountConfirmation, new Progress<OperationProgress>(ReportProgress)), cancellationToken).ConfigureAwait(false);
                WriteJson(result);
                return 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return 130; }
            catch (Exception exception) { WriteError(exception); return 1; }
        });

        var train = new Command("train", "Start or resume Seed-VC fine-tuning with explicit arguments.");
        var prep = new Option<string>("--prep") { Required = true, Description = "Seed-VC preparation directory." };
        var trainRoot = new Option<string?>("--seedvc-root") { Description = "Seed-VC checkout directory (defaults to global config)." };
        var trainPython = new Option<string?>("--python");
        var trainConfig = new Option<string?>("--config");
        var trainOut = new Option<string?>("--out");
        var runName = new Option<string?>("--run-name");
        var noResume = new Option<bool>("--no-resume") { Description = "Fail if the run already exists instead of reusing it." };
        var batch = new Option<int>("--batch-size") { DefaultValueFactory = _ => 1 };
        var steps = new Option<int>("--max-steps") { DefaultValueFactory = _ => 1000 };
        var epochs = new Option<int>("--max-epochs") { DefaultValueFactory = _ => 1000 };
        var saveEvery = new Option<int>("--save-every") { DefaultValueFactory = _ => 500 };
        train.Options.Add(prep); train.Options.Add(trainRoot); train.Options.Add(trainPython); train.Options.Add(trainConfig); train.Options.Add(trainOut); train.Options.Add(runName); train.Options.Add(noResume); train.Options.Add(batch); train.Options.Add(steps); train.Options.Add(epochs); train.Options.Add(saveEvery);
        train.SetAction(async (parseResult, cancellationToken) =>
        {
            try
            {
                await using var workflowRoot = CreateRoot();
                var result = await workflowRoot.SeedVc.TrainAsync(
                    new SeedVcTrainRequest(parseResult.GetValue(prep)!, parseResult.GetValue(trainRoot), parseResult.GetValue(trainPython), parseResult.GetValue(trainConfig), parseResult.GetValue(trainOut), parseResult.GetValue(runName), parseResult.GetValue(batch), parseResult.GetValue(steps), parseResult.GetValue(epochs), parseResult.GetValue(saveEvery), !parseResult.GetValue(noResume)),
                    new WorkflowContext(workflowRoot.AccountConfirmation, new Progress<OperationProgress>(ReportProgress)), cancellationToken).ConfigureAwait(false);
                WriteJson(result);
                return result.Status == SeedVcTrainStatus.Completed ? 0 : 1;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return 130; }
            catch (Exception exception) { WriteError(exception); return 1; }
        });

        var infer = new Command("infer", "Convert a source recording to the trained speaker voice.");
        var inferRoot = new Option<string?>("--seedvc-root") { Description = "Seed-VC checkout directory (defaults to global config)." };
        var source = new Option<string>("--source") { Required = true };
        var reference = new Option<string>("--reference") { Required = true };
        var checkpoint = new Option<string>("--checkpoint") { Required = true };
        var inferConfig = new Option<string?>("--config");
        var inferPython = new Option<string?>("--python");
        var inferOut = new Option<string?>("--out");
        var inferRun = new Option<string?>("--run-name");
        var diffusion = new Option<int>("--diffusion-steps") { DefaultValueFactory = _ => 50 };
        infer.Options.Add(inferRoot); infer.Options.Add(source); infer.Options.Add(reference); infer.Options.Add(checkpoint); infer.Options.Add(inferConfig); infer.Options.Add(inferPython); infer.Options.Add(inferOut); infer.Options.Add(inferRun); infer.Options.Add(diffusion);
        infer.SetAction(async (parseResult, cancellationToken) =>
        {
            try
            {
                await using var workflowRoot = CreateRoot();
                var result = await workflowRoot.SeedVc.InferAsync(
                    new SeedVcInferRequest(parseResult.GetValue(inferRoot), parseResult.GetValue(source)!, parseResult.GetValue(reference)!, parseResult.GetValue(checkpoint)!, parseResult.GetValue(inferConfig), parseResult.GetValue(inferPython), parseResult.GetValue(inferOut), parseResult.GetValue(inferRun), parseResult.GetValue(diffusion)),
                    new WorkflowContext(workflowRoot.AccountConfirmation, new Progress<OperationProgress>(ReportProgress)), cancellationToken).ConfigureAwait(false);
                WriteJson(result);
                return result.Status == SeedVcInferStatus.Completed ? 0 : 1;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return 130; }
            catch (Exception exception) { WriteError(exception); return 1; }
        });

        var globalConfig = new Command("config", "Show or update the shared global Seed-VC/Linux toolchain configuration.");
        var showConfig = new Command("show", "Show the resolved global toolchain settings.");
        showConfig.SetAction(parseResult =>
        {
            var resolver = new SeedVcToolchainResolver();
            WriteJson(new { Path = resolver.GlobalConfigPath, Settings = resolver.Load(), Resolved = resolver.Resolve() });
            return 0;
        });
        var setConfig = new Command("set", "Persist toolchain paths and the optional OpenSSH Linux host alias.");
        var setRoot = new Option<string?>("--seedvc-root");
        var setPython = new Option<string?>("--python");
        var setFfmpeg = new Option<string?>("--ffmpeg");
        var setSeedConfig = new Option<string?>("--config");
        var linuxHost = new Option<string?>("--linux-host") { Description = "OpenSSH host alias from ~/.ssh/config (for example chatapp-linux)." };
        var linuxUser = new Option<string?>("--linux-user");
        var linuxPort = new Option<int?>("--linux-port");
        var linuxRoot = new Option<string?>("--linux-seedvc-root");
        var linuxPython = new Option<string?>("--linux-python");
        var linuxFfmpeg = new Option<string?>("--linux-ffmpeg");
        foreach (var option in new Option[] { setRoot, setPython, setFfmpeg, setSeedConfig, linuxHost, linuxUser, linuxPort, linuxRoot, linuxPython, linuxFfmpeg }) setConfig.Options.Add(option);
        setConfig.SetAction(parseResult =>
        {
            var resolver = new SeedVcToolchainResolver();
            var current = resolver.Load();
            var updated = current with
            {
                SeedVcRoot = parseResult.GetValue(setRoot) ?? current.SeedVcRoot,
                PythonPath = parseResult.GetValue(setPython) ?? current.PythonPath,
                FfmpegPath = parseResult.GetValue(setFfmpeg) ?? current.FfmpegPath,
                ConfigPath = parseResult.GetValue(setSeedConfig) ?? current.ConfigPath,
                LinuxHost = parseResult.GetValue(linuxHost) ?? current.LinuxHost,
                LinuxUser = parseResult.GetValue(linuxUser) ?? current.LinuxUser,
                LinuxPort = parseResult.GetValue(linuxPort) ?? current.LinuxPort,
                LinuxSeedVcRoot = parseResult.GetValue(linuxRoot) ?? current.LinuxSeedVcRoot,
                LinuxPythonPath = parseResult.GetValue(linuxPython) ?? current.LinuxPythonPath,
                LinuxFfmpegPath = parseResult.GetValue(linuxFfmpeg) ?? current.LinuxFfmpegPath,
            };
            resolver.Save(updated);
            WriteJson(new { Path = resolver.GlobalConfigPath, Settings = resolver.Load(), Resolved = resolver.Resolve() });
            return 0;
        });
        globalConfig.Subcommands.Add(showConfig); globalConfig.Subcommands.Add(setConfig);
        command.Subcommands.Add(doctor); command.Subcommands.Add(remote); command.Subcommands.Add(prepare); command.Subcommands.Add(train); command.Subcommands.Add(infer); command.Subcommands.Add(globalConfig);
        return command;
    }
}
