using System.CommandLine;
using WeChatVoice.Core.Models;
using WeChatVoice.Infrastructure.Sqlite;
using WeChatVoice.Workflows.Workflows;

namespace WeChatVoice.Cli;

internal static partial class CliApplication
{
    static Command CreateDatasetCommand()
    {
        var datasetCommand = new Command("dataset", "Discover and audit a decrypted WeChat database bundle.");
        var probeCommand = new Command("probe", "Discover message/media/contact databases and probe their schemas.");
        var rootOption = new Option<string>("--root")
        {
            Description = "Root directory containing decrypted database files.",
            Required = true,
        };
        var shareableOutputOption = new Option<string>("--shareable-output")
        {
            Description = "Shareable JSON data-set probe output.",
            Required = true,
        };
        var snapshotManifestOption = new Option<string?>("--snapshot-manifest")
        {
            Description = "Optional existing snapshot manifest whose DB/WAL/SHM hashes can be reused.",
        };

        probeCommand.Options.Add(rootOption);
        probeCommand.Options.Add(shareableOutputOption);
        probeCommand.Options.Add(snapshotManifestOption);
        probeCommand.SetAction(async (parseResult, cancellationToken) =>
        {
            var root = parseResult.GetValue(rootOption);
            var output = parseResult.GetValue(shareableOutputOption);
            var snapshotManifestPath = parseResult.GetValue(snapshotManifestOption);
            if (root is null || output is null)
            {
                Console.Error.WriteLine("Both --root and --shareable-output are required.");
                return 2;
            }

            try
            {
                var probe = await new DataSetProbeService().ProbeAsync(
                    root,
                    new DataSetProbeOptions(
                        IncludeLocalPaths: false,
                        SnapshotManifest: snapshotManifestPath is null
                            ? null
                            : await ReadJsonFileAsync<SnapshotManifest>(snapshotManifestPath, cancellationToken).ConfigureAwait(false)),
                    cancellationToken).ConfigureAwait(false);
                await WriteJsonFileAsync(output, probe, cancellationToken).ConfigureAwait(false);
                WriteJson(new DatasetProbeResult(
                    Path.GetFullPath(output),
                    probe.DataSet.DataSetId,
                    probe.DataSet.Databases.Count,
                    probe.Issues.Count,
                    probe.AdapterCandidates.Count));
                return 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Console.Error.WriteLine("Data-set probing was cancelled.");
                return 130;
            }
            catch (Exception exception)
            {
                WriteError(exception);
                return 1;
            }
        });

        datasetCommand.Subcommands.Add(probeCommand);

        var buildCommand = new Command("build", "Build a derived training dataset from a verified export selection profile.");
        var exportRootOption = new Option<string>("--export") { Description = "Export root containing manifest.private.json and selection-profile.json.", Required = true };
        var profileOption = new Option<string?>("--profile") { Description = "Optional selection-profile.json path." };
        var manifestOption = new Option<string?>("--manifest") { Description = "Optional private export manifest path." };
        var outputOption = new Option<string?>("--output") { Description = "Optional curated dataset output directory." };
        buildCommand.Options.Add(exportRootOption);
        buildCommand.Options.Add(profileOption);
        buildCommand.Options.Add(manifestOption);
        buildCommand.Options.Add(outputOption);
        buildCommand.SetAction(async (parseResult, cancellationToken) =>
        {
            try
            {
                await using var root = CreateRoot();
                var result = await root.DatasetCuration.BuildDatasetAsync(
                    new DatasetBuildRequest(
                        parseResult.GetValue(exportRootOption)!,
                        parseResult.GetValue(profileOption),
                        parseResult.GetValue(manifestOption),
                        parseResult.GetValue(outputOption)),
                    new WorkflowContext(root.AccountConfirmation, new Progress<OperationProgress>(ReportProgress)),
                    cancellationToken).ConfigureAwait(false);
                WriteJson(result);
                return 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Console.Error.WriteLine("Dataset build was cancelled.");
                return 130;
            }
            catch (Exception exception)
            {
                WriteError(exception);
                return 1;
            }
        });
        datasetCommand.Subcommands.Add(buildCommand);

        var verifyCommand = new Command("verify", "Verify a curated dataset's audio hashes and derived metadata without modifying it.");
        var verifyExportOption = new Option<string>("--export") { Description = "Export root containing the private manifest and selection profile.", Required = true };
        var verifyOutputOption = new Option<string>("--output") { Description = "Curated dataset output directory.", Required = true };
        var verifyProfileOption = new Option<string?>("--profile") { Description = "Optional selection-profile.json path." };
        var verifyManifestOption = new Option<string?>("--manifest") { Description = "Optional private export manifest path." };
        verifyCommand.Options.Add(verifyExportOption);
        verifyCommand.Options.Add(verifyOutputOption);
        verifyCommand.Options.Add(verifyProfileOption);
        verifyCommand.Options.Add(verifyManifestOption);
        verifyCommand.SetAction(async (parseResult, cancellationToken) =>
        {
            try
            {
                await using var root = CreateRoot();
                var result = await root.DatasetCuration.VerifyDatasetAsync(
                    new DatasetBuildRequest(
                        parseResult.GetValue(verifyExportOption)!,
                        parseResult.GetValue(verifyProfileOption),
                        parseResult.GetValue(verifyManifestOption),
                        parseResult.GetValue(verifyOutputOption)),
                    new WorkflowContext(root.AccountConfirmation, new Progress<OperationProgress>(ReportProgress)),
                    cancellationToken).ConfigureAwait(false);
                WriteJson(result);
                return result.IsValid ? 0 : 1;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Console.Error.WriteLine("Dataset verification was cancelled.");
                return 130;
            }
            catch (Exception exception)
            {
                WriteError(exception);
                return 1;
            }
        });
        datasetCommand.Subcommands.Add(verifyCommand);

        var repairCommand = new Command("repair", "Regenerate curated dataset metadata after verifying existing audio; never changes SILK files.");
        var repairExportOption = new Option<string>("--export") { Description = "Export root containing the private manifest and selection profile.", Required = true };
        var repairOutputOption = new Option<string>("--output") { Description = "Curated dataset output directory.", Required = true };
        var repairProfileOption = new Option<string?>("--profile") { Description = "Optional selection-profile.json path." };
        var repairManifestOption = new Option<string?>("--manifest") { Description = "Optional private export manifest path." };
        repairCommand.Options.Add(repairExportOption);
        repairCommand.Options.Add(repairOutputOption);
        repairCommand.Options.Add(repairProfileOption);
        repairCommand.Options.Add(repairManifestOption);
        repairCommand.SetAction(async (parseResult, cancellationToken) =>
        {
            try
            {
                await using var root = CreateRoot();
                var result = await root.DatasetCuration.RepairDatasetAsync(
                    new DatasetBuildRepairRequest(
                        parseResult.GetValue(repairExportOption)!,
                        parseResult.GetValue(repairOutputOption)!,
                        parseResult.GetValue(repairProfileOption),
                        parseResult.GetValue(repairManifestOption)),
                    new WorkflowContext(root.AccountConfirmation, new Progress<OperationProgress>(ReportProgress)),
                    cancellationToken).ConfigureAwait(false);
                WriteJson(result);
                return result.IsValid ? 0 : 1;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Console.Error.WriteLine("Dataset repair was cancelled.");
                return 130;
            }
            catch (Exception exception)
            {
                WriteError(exception);
                return 1;
            }
        });
        datasetCommand.Subcommands.Add(repairCommand);
        return datasetCommand;
    }

}
