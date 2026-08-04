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
        var linkedViewOption = new Option<bool>("--linked-view") { Description = "Advanced/non-portable: use hard links instead of independent copies." };
        buildCommand.Options.Add(exportRootOption);
        buildCommand.Options.Add(profileOption);
        buildCommand.Options.Add(manifestOption);
        buildCommand.Options.Add(outputOption);
        buildCommand.Options.Add(linkedViewOption);
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
                        parseResult.GetValue(outputOption),
                        parseResult.GetValue(linkedViewOption) ? DatasetLinkMode.LinkedView : DatasetLinkMode.VerifiedCopy),
                    new WorkflowContext(root.AccountConfirmation, new Progress<OperationProgress>(ReportProgress)),
                    cancellationToken).ConfigureAwait(false);
                WriteJson(result);
                if (result.LinkMode == DatasetLinkMode.LinkedView)
                {
                    Console.Error.WriteLine("WARNING: Linked View uses hard links, is not an independent portable dataset, and is marked read-only.");
                }
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

        var deleteCommand = new Command("delete", "Delete only a verified derived curated dataset; never the export or source workspace.");
        var deleteExportOption = new Option<string>("--export") { Description = "Export root containing the private manifest and profile.", Required = true };
        var deleteOutputOption = new Option<string>("--output") { Description = "Curated dataset output directory.", Required = true };
        var deleteFingerprintOption = new Option<string>("--selection-fingerprint") { Description = "Exact curation Selection Fingerprint.", Required = true };
        var deleteYesOption = new Option<bool>("--yes") { Description = "Confirm the second destructive deletion step." };
        deleteCommand.Options.Add(deleteExportOption);
        deleteCommand.Options.Add(deleteOutputOption);
        deleteCommand.Options.Add(deleteFingerprintOption);
        deleteCommand.Options.Add(deleteYesOption);
        deleteCommand.SetAction(async (parseResult, cancellationToken) =>
        {
            try
            {
                await using var root = CreateRoot();
                var request = new DatasetDeleteRequest(
                    parseResult.GetValue(deleteExportOption)!,
                    parseResult.GetValue(deleteOutputOption)!,
                    parseResult.GetValue(deleteFingerprintOption)!,
                    parseResult.GetValue(deleteYesOption));
                var context = new WorkflowContext(root.AccountConfirmation, new Progress<OperationProgress>(ReportProgress));
                if (!request.Confirmed)
                {
                    var preview = await root.DatasetCuration.PreviewDeleteDatasetAsync(request, context, cancellationToken).ConfigureAwait(false);
                    WriteJson(preview);
                    Console.Error.WriteLine("Second confirmation required: rerun with --yes.");
                    return 2;
                }

                var result = await root.DatasetCuration.DeleteDatasetAsync(request, context, cancellationToken).ConfigureAwait(false);
                WriteJson(result);
                return 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Console.Error.WriteLine("Dataset deletion was cancelled.");
                return 130;
            }
            catch (Exception exception)
            {
                WriteError(exception);
                return 1;
            }
        });
        buildCommand.Subcommands.Add(deleteCommand);
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
