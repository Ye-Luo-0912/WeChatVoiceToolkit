using System.CommandLine;
using WeChatVoice.Core.Models;
using WeChatVoice.Infrastructure.Sqlite;

namespace WeChatVoice.Cli;

internal static partial class CliApplication
{
    static Command CreateSchemaCommand()
    {
        var schemaCommand = new Command("schema", "Inspect SQLite structure without interpreting data.");
        var probeCommand = new Command("probe", "Write tables, views, columns, and raw DDL to JSON.");
        var databaseOption = new Option<string>("--database")
        {
            Description = "SQLite database to inspect in read-only mode.",
            Required = true,
        };
        var outputOption = new Option<string>("--output")
        {
            Description = "JSON output file. Existing files are replaced atomically.",
            Required = true,
        };
        var includeLocalPathsOption = new Option<bool>("--include-local-paths")
        {
            Description = "Include absolute local paths; omitted by default for shareable schema JSON.",
        };

        probeCommand.Options.Add(databaseOption);
        probeCommand.Options.Add(outputOption);
        probeCommand.Options.Add(includeLocalPathsOption);
        probeCommand.SetAction(async (parseResult, cancellationToken) =>
        {
            var database = parseResult.GetValue(databaseOption);
            var output = parseResult.GetValue(outputOption);
            var includeLocalPaths = parseResult.GetValue(includeLocalPathsOption);

            if (database is null || output is null)
            {
                Console.Error.WriteLine("Both --database and --output are required.");
                return 2;
            }

            try
            {
                var snapshot = await new SqliteSchemaInspector().InspectAsync(
                    database,
                    new SchemaInspectionOptions(includeLocalPaths),
                    cancellationToken).ConfigureAwait(false);
                await WriteJsonFileAsync(output, snapshot, cancellationToken).ConfigureAwait(false);
                WriteJson(new SchemaProbeResult(Path.GetFullPath(output), snapshot.Objects.Count));
                return 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Console.Error.WriteLine("Schema probing was cancelled.");
                return 130;
            }
            catch (Exception exception)
            {
                WriteError(exception);
                return 1;
            }
        });

        schemaCommand.Subcommands.Add(probeCommand);
        return schemaCommand;
    }

}
