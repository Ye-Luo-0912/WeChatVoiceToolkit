using System.CommandLine;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using WeChatVoice.Core.Models;
using WeChatVoice.Infrastructure.Snapshots;
using WeChatVoice.Infrastructure.Sqlite;
using WeChatVoice.Windows;

var rootCommand = new RootCommand("Safe, schema-agnostic WeChat voice toolkit foundation.");
rootCommand.Subcommands.Add(CreateDoctorCommand());
rootCommand.Subcommands.Add(CreateSnapshotCommand());
rootCommand.Subcommands.Add(CreateSchemaCommand());

return rootCommand.Parse(args).Invoke();

static Command CreateDoctorCommand()
{
    var command = new Command("doctor", "Report the local runtime and the deliberately limited capabilities.");
    command.SetAction(_ =>
    {
        var report = new DoctorReport(
            RuntimeInformation.FrameworkDescription,
            Environment.OSVersion.VersionString,
            OperatingSystem.IsWindows(),
            WeChatProcessDiscovery.SupportedProcessNames,
            WeChatProcessDiscovery.ListRunning(),
            new SecurityBoundary(
                HasSchemaAdapter: false,
                AllowsKeyScanning: false,
                AllowsDatabaseDecryption: false,
                AllowsArbitraryProcessMemoryRead: false,
                HasUserInterface: false));

        WriteJson(report);
        return 0;
    });
    return command;
}

static Command CreateSnapshotCommand()
{
    var snapshotCommand = new Command("snapshot", "Create a stable file-level snapshot before inspection.");
    var createCommand = new Command("create", "Recursively copy files and produce snapshot.json.");
    var sourceOption = new Option<string>("--source")
    {
        Description = "Source directory containing database files.",
        Required = true,
    };
    var outputOption = new Option<string>("--output")
    {
        Description = "New output directory for the completed snapshot.",
        Required = true,
    };
    var allowUnstableOption = new Option<bool>("--allow-unstable")
    {
        Description = "Allow a source file to change while it is copied (not recommended).",
    };

    createCommand.Options.Add(sourceOption);
    createCommand.Options.Add(outputOption);
    createCommand.Options.Add(allowUnstableOption);
    createCommand.SetAction(async (parseResult, cancellationToken) =>
    {
        var source = parseResult.GetValue(sourceOption);
        var output = parseResult.GetValue(outputOption);
        var allowUnstable = parseResult.GetValue(allowUnstableOption);

        if (source is null || output is null)
        {
            Console.Error.WriteLine("Both --source and --output are required.");
            return 2;
        }

        try
        {
            var request = new SnapshotRequest(source, output, RequireStableSource: !allowUnstable);
            var manifest = await new SnapshotCreator().CreateAsync(request, cancellationToken).ConfigureAwait(false);
            WriteJson(manifest);
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Console.Error.WriteLine("Snapshot creation was cancelled.");
            return 130;
        }
        catch (Exception exception)
        {
            WriteError(exception);
            return 1;
        }
    });

    snapshotCommand.Subcommands.Add(createCommand);
    return snapshotCommand;
}

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

    probeCommand.Options.Add(databaseOption);
    probeCommand.Options.Add(outputOption);
    probeCommand.SetAction(async (parseResult, cancellationToken) =>
    {
        var database = parseResult.GetValue(databaseOption);
        var output = parseResult.GetValue(outputOption);

        if (database is null || output is null)
        {
            Console.Error.WriteLine("Both --database and --output are required.");
            return 2;
        }

        try
        {
            var snapshot = await new SqliteSchemaInspector().InspectAsync(database, cancellationToken).ConfigureAwait(false);
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

static async Task WriteJsonFileAsync<T>(string outputPath, T value, CancellationToken cancellationToken)
{
    ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

    var fullOutputPath = Path.GetFullPath(outputPath);
    var directory = Path.GetDirectoryName(fullOutputPath)
        ?? throw new ArgumentException("The output path must include a directory.", nameof(outputPath));
    Directory.CreateDirectory(directory);

    var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullOutputPath)}.{Guid.NewGuid():N}.tmp");
    try
    {
        await using (var stream = new FileStream(
            temporaryPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await JsonSerializer.SerializeAsync(stream, value, CliJson.Options, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporaryPath, fullOutputPath, overwrite: true);
    }
    finally
    {
        if (File.Exists(temporaryPath))
        {
            File.Delete(temporaryPath);
        }
    }
}

static void WriteJson<T>(T value) =>
    Console.WriteLine(JsonSerializer.Serialize(value, CliJson.Options));

static void WriteError(Exception exception) =>
    Console.Error.WriteLine($"{exception.GetType().Name}: {exception.Message}");

internal sealed record DoctorReport(
    string Runtime,
    string OperatingSystem,
    bool IsWindows,
    IReadOnlyList<string> RecognizedProcessNames,
    IReadOnlyList<WeChatProcessInfo> RunningWeChatProcesses,
    SecurityBoundary Security);

internal sealed record SecurityBoundary(
    bool HasSchemaAdapter,
    bool AllowsKeyScanning,
    bool AllowsDatabaseDecryption,
    bool AllowsArbitraryProcessMemoryRead,
    bool HasUserInterface);

internal sealed record SchemaProbeResult(string OutputPath, int ObjectCount);

internal static class CliJson
{
    internal static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
