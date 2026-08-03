using System.CommandLine;
using WeChatVoice.Core.Models;
using WeChatVoice.Infrastructure.Sqlite;

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
        return datasetCommand;
    }

}
