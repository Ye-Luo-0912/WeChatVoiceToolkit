using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using WeChatVoice.Application;
using WeChatVoice.Cli.Services;
using WeChatVoice.Core.Models;
using WeChatVoice.Core.Ports;
using WeChatVoice.Infrastructure.Adapters;
using WeChatVoice.Infrastructure.Export;
using WeChatVoice.Infrastructure.Materialization;
using WeChatVoice.Infrastructure.Snapshots;
using WeChatVoice.Infrastructure.Sqlite;
using WeChatVoice.Windows;

var rootCommand = new RootCommand("Safe, schema-agnostic WeChat voice toolkit foundation.");
rootCommand.Subcommands.Add(CreateDoctorCommand());
rootCommand.Subcommands.Add(CreateSnapshotCommand());
rootCommand.Subcommands.Add(CreateSchemaCommand());
rootCommand.Subcommands.Add(CreateVoiceCommand());
rootCommand.Subcommands.Add(CreateDatasetCommand());
rootCommand.Subcommands.Add(CreateWorkspaceCommand());
rootCommand.Subcommands.Add(CreateContactCommand());

return rootCommand.Parse(args).Invoke();

static Command CreateDoctorCommand()
{
    var command = new Command("doctor", "Report the local runtime and the deliberately limited capabilities.");
    command.SetAction(_ =>
    {
        var report = new DoctorReport(
            System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            Environment.OSVersion.VersionString,
            OperatingSystem.IsWindows(),
            WeChatProcessDiscovery.SupportedProcessNames,
            WeChatProcessDiscovery.ListRunning(),
            new SecurityBoundary(
                HasSchemaAdapter: BuiltInAdapters.Create().Count > 0,
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
    var allowLiveSourceOption = new Option<bool>("--allow-live-source")
    {
        Description = "Explicitly allow a live WeChat source; the manifest is marked potentiallyInconsistent.",
    };
    var maxAttemptsOption = new Option<int>("--max-attempts")
    {
        Description = "Maximum group-level snapshot attempts when the source changes.",
        DefaultValueFactory = _ => 3,
    };

    createCommand.Options.Add(sourceOption);
    createCommand.Options.Add(outputOption);
    createCommand.Options.Add(allowLiveSourceOption);
    createCommand.Options.Add(maxAttemptsOption);
    createCommand.SetAction(async (parseResult, cancellationToken) =>
    {
        var source = parseResult.GetValue(sourceOption);
        var output = parseResult.GetValue(outputOption);
        var allowLiveSource = parseResult.GetValue(allowLiveSourceOption);
        var maxAttempts = parseResult.GetValue(maxAttemptsOption);

        if (source is null || output is null)
        {
            Console.Error.WriteLine("Both --source and --output are required.");
            return 2;
        }

        try
        {
            var request = new SnapshotRequest(source, output, AllowLiveSource: allowLiveSource, MaxAttempts: maxAttempts);
            var manifest = await new SnapshotCreator(new WeChatSnapshotSourceActivityProbe()).CreateAsync(request, cancellationToken).ConfigureAwait(false);
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

static Command CreateVoiceCommand()
{
    var voiceCommand = new Command("voice", "Work with verified WeChat voice data sets.");
    var exportCommand = new Command("export", "Export voice payloads from a verified data-set adapter.");
    var scanCommand = new Command("scan", "Audit matching voice metadata without writing payload files.");
    var workspaceOption = new Option<string>("--workspace")
    {
        Description = "Local executable workspace JSON containing absolute database paths.",
    };
    var outputOption = new Option<string>("--output")
    {
        Description = "Export root directory.",
    };
    var conversationOption = new Option<string?>("--conversation-id")
    {
        Description = "Optional conversation filter.",
    };
    var contactUsernameOption = new Option<string?>("--contact-username")
    {
        Description = "Stable internal username used for exact contact selection.",
    };
    var directionOption = new Option<string?>("--direction")
    {
        Description = "Voice direction: incoming or outgoing.",
    };
    var fromOption = new Option<string?>("--from")
    {
        Description = "Inclusive UTC start date/time.",
    };
    var toOption = new Option<string?>("--to")
    {
        Description = "Inclusive UTC end date/time.",
    };
    var formatOption = new Option<string>("--format")
    {
        Description = "Export format. The first available chain supports silk only.",
        DefaultValueFactory = _ => "silk",
    };
    exportCommand.Options.Add(workspaceOption);
    exportCommand.Options.Add(outputOption);
    exportCommand.Options.Add(conversationOption);
    exportCommand.Options.Add(contactUsernameOption);
    exportCommand.Options.Add(directionOption);
    exportCommand.Options.Add(fromOption);
    exportCommand.Options.Add(toOption);
    exportCommand.Options.Add(formatOption);
    exportCommand.SetAction(async (parseResult, cancellationToken) =>
    {
        var workspacePath = parseResult.GetValue(workspaceOption);
        var output = parseResult.GetValue(outputOption);
        var conversationId = parseResult.GetValue(conversationOption);
        var contactUsername = parseResult.GetValue(contactUsernameOption);
        var directionText = parseResult.GetValue(directionOption);
        var fromText = parseResult.GetValue(fromOption);
        var toText = parseResult.GetValue(toOption);
        var format = parseResult.GetValue(formatOption);

        if (workspacePath is null || output is null)
        {
            Console.Error.WriteLine("Both --workspace and --output are required.");
            return 2;
        }

        if (!string.Equals(format, "silk", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("Only --format silk is supported in the first export chain.");
            return 2;
        }

        try
        {
            await using var catalog = await OpenCatalogAsync(workspacePath, cancellationToken).ConfigureAwait(false);
            var contact = await new ContactResolver().ResolveExactAsync(catalog, contactUsername, cancellationToken).ConfigureAwait(false);
            var service = new VoiceExportService(catalog, new FileSystemVoiceExportStore(output));
            var query = BuildVoiceQuery(conversationId, contact, directionText, fromText, toText);
            var manifest = await service.ExportAsync(query, new VoiceExportOptions { DecodeToWav = false }, cancellationToken).ConfigureAwait(false);
            WriteJson(manifest);
            return GetExportExitCode(manifest);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Console.Error.WriteLine("Voice export was cancelled.");
            return 130;
        }
        catch (ArgumentException exception)
        {
            WriteError(exception);
            return 2;
        }
        catch (Exception exception)
        {
            WriteError(exception);
            return 1;
        }
    });

    var scanWorkspaceOption = new Option<string>("--workspace") { Description = "Local executable workspace JSON.", Required = true };
    var scanContactOption = new Option<string?>("--contact-username") { Description = "Stable internal username used for exact contact selection." };
    var scanDirectionOption = new Option<string?>("--direction") { Description = "Voice direction: incoming or outgoing." };
    var scanFromOption = new Option<string?>("--from") { Description = "Inclusive UTC start date/time." };
    var scanToOption = new Option<string?>("--to") { Description = "Inclusive UTC end date/time." };
    var scanConversationOption = new Option<string?>("--conversation-id") { Description = "Optional conversation filter." };
    scanCommand.Options.Add(scanWorkspaceOption);
    scanCommand.Options.Add(scanContactOption);
    scanCommand.Options.Add(scanDirectionOption);
    scanCommand.Options.Add(scanFromOption);
    scanCommand.Options.Add(scanToOption);
    scanCommand.Options.Add(scanConversationOption);
    scanCommand.SetAction(async (parseResult, cancellationToken) =>
    {
        var workspace = parseResult.GetValue(scanWorkspaceOption);
        if (workspace is null)
        {
            Console.Error.WriteLine("--workspace is required.");
            return 2;
        }

        try
        {
            await using var catalog = await OpenCatalogAsync(workspace, cancellationToken).ConfigureAwait(false);
            var contact = await new ContactResolver().ResolveExactAsync(catalog, parseResult.GetValue(scanContactOption), cancellationToken).ConfigureAwait(false);
            var query = BuildVoiceQuery(
                parseResult.GetValue(scanConversationOption),
                contact,
                parseResult.GetValue(scanDirectionOption),
                parseResult.GetValue(scanFromOption),
                parseResult.GetValue(scanToOption));
            var report = await new VoiceScanService(catalog).ScanAsync(query, cancellationToken).ConfigureAwait(false);
            WriteJson(report);
            return report.MatchedVoiceCount == 0 ? 4 : 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Console.Error.WriteLine("Voice scan was cancelled.");
            return 130;
        }
        catch (ArgumentException exception)
        {
            WriteError(exception);
            return 2;
        }
        catch (Exception exception)
        {
            WriteError(exception);
            return 1;
        }
    });

    voiceCommand.Subcommands.Add(exportCommand);
    voiceCommand.Subcommands.Add(scanCommand);

    var recoverCommand = new Command("recover", "Rebuild a manifest from a flushed export Journal after a process crash.");
    var journalOption = new Option<string>("--journal")
    {
        Description = "runs/<run-id>.jsonl Journal to recover.",
        Required = true,
    };
    recoverCommand.Options.Add(journalOption);
    recoverCommand.SetAction(async (parseResult, cancellationToken) =>
    {
        var journalPath = parseResult.GetValue(journalOption);
        if (journalPath is null)
        {
            Console.Error.WriteLine("--journal is required.");
            return 2;
        }

        try
        {
            var fullJournalPath = Path.GetFullPath(journalPath);
            var exportRoot = Path.GetDirectoryName(Path.GetDirectoryName(fullJournalPath)!)
                ?? throw new InvalidDataException("The Journal path must be nested under an export root runs directory.");
            var manifest = await new FileSystemVoiceExportStore(exportRoot).RecoverRunAsync(fullJournalPath, cancellationToken).ConfigureAwait(false);
            WriteJson(manifest);
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Console.Error.WriteLine("Export Journal recovery was cancelled.");
            return 130;
        }
        catch (Exception exception)
        {
            WriteError(exception);
            return 1;
        }
    });
    exportCommand.Subcommands.Add(recoverCommand);
    return voiceCommand;
}

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

static Command CreateWorkspaceCommand()
{
    var workspaceCommand = new Command("workspace", "Create an executable local database workspace.");
    var createCommand = new Command("create", "Probe a decrypted database root and retain local paths for execution.");
    var verifyCommand = new Command("verify", "Verify that a local workspace still points at the unchanged database bundle.");
    var materializeCommand = new Command("materialize", "Run a fixed external decryptor and validate ordinary SQLite output.");
    var rootOption = new Option<string>("--root")
    {
        Description = "Root directory containing decrypted database files.",
        Required = true,
    };
    var outputOption = new Option<string>("--output")
    {
        Description = "Local workspace JSON, for example .wechatvoice/local-workspace.json.",
        Required = true,
    };

    createCommand.Options.Add(rootOption);
    createCommand.Options.Add(outputOption);
    createCommand.SetAction(async (parseResult, cancellationToken) =>
    {
        var root = parseResult.GetValue(rootOption);
        var output = parseResult.GetValue(outputOption);
        if (root is null || output is null)
        {
            Console.Error.WriteLine("Both --root and --output are required.");
            return 2;
        }

        try
        {
            var workspace = await new LocalWorkspaceCreator().CreateAsync(root, cancellationToken).ConfigureAwait(false);
            await WriteJsonFileAsync(output, workspace, cancellationToken).ConfigureAwait(false);
            WriteJson(new WorkspaceCreateResult(
                Path.GetFullPath(output),
                workspace.WorkspaceId,
                workspace.DataSet.DataSetId,
                workspace.DataSet.Databases.Count,
                workspace.Issues.Count));
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Console.Error.WriteLine("Workspace creation was cancelled.");
            return 130;
        }
        catch (Exception exception)
        {
            WriteError(exception);
            return 1;
        }
    });

    workspaceCommand.Subcommands.Add(createCommand);

    var verifyWorkspaceOption = new Option<string>("--workspace")
    {
        Description = "Local executable workspace JSON.",
        Required = true,
    };
    verifyCommand.Options.Add(verifyWorkspaceOption);
    verifyCommand.SetAction(async (parseResult, cancellationToken) =>
    {
        var workspacePath = parseResult.GetValue(verifyWorkspaceOption);
        if (workspacePath is null)
        {
            Console.Error.WriteLine("--workspace is required.");
            return 2;
        }

        try
        {
            var verified = await new WorkspaceLoader().LoadVerifiedAsync(workspacePath, cancellationToken).ConfigureAwait(false);
            WriteJson(new WorkspaceVerifyResult(
                Path.GetFullPath(workspacePath),
                verified.Workspace.WorkspaceId,
                verified.DataSet.DataSetId,
                verified.DataSet.Databases.Count,
                verified.VerifiedAtUtc));
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Console.Error.WriteLine("Workspace verification was cancelled.");
            return 130;
        }
        catch (Exception exception)
        {
            WriteError(exception);
            return 1;
        }
    });
    workspaceCommand.Subcommands.Add(verifyCommand);

    var snapshotDirectoryOption = new Option<string>("--snapshot-directory")
    {
        Description = "Raw snapshot directory produced by snapshot create.",
        Required = true,
    };
    var snapshotManifestOption = new Option<string?>("--snapshot-manifest")
    {
        Description = "Optional snapshot manifest; defaults to .wechatvoice/snapshot-manifest.json under the snapshot directory.",
    };
    var decryptorOption = new Option<string>("--external-decryptor")
    {
        Description = "Fixed external decryptor executable implementing --input-root/--output-root/--key-file.",
        Required = true,
    };
    var keyFileOption = new Option<string?>("--key-file")
    {
        Description = "Optional key file passed as a fixed decryptor argument.",
    };
    var materializedOutputOption = new Option<string>("--output")
    {
        Description = "New ordinary SQLite output directory.",
        Required = true,
    };
    var workspaceOutputOption = new Option<string?>("--workspace-output")
    {
        Description = "Local workspace JSON; defaults to .wechatvoice/local-workspace.json under the materialized output.",
    };
    materializeCommand.Options.Add(snapshotDirectoryOption);
    materializeCommand.Options.Add(snapshotManifestOption);
    materializeCommand.Options.Add(decryptorOption);
    materializeCommand.Options.Add(keyFileOption);
    materializeCommand.Options.Add(materializedOutputOption);
    materializeCommand.Options.Add(workspaceOutputOption);
    materializeCommand.SetAction(async (parseResult, cancellationToken) =>
    {
        var snapshotDirectory = parseResult.GetValue(snapshotDirectoryOption);
        var snapshotManifest = parseResult.GetValue(snapshotManifestOption);
        var decryptor = parseResult.GetValue(decryptorOption);
        var keyFile = parseResult.GetValue(keyFileOption);
        var output = parseResult.GetValue(materializedOutputOption);
        var workspaceOutput = parseResult.GetValue(workspaceOutputOption);
        if (snapshotDirectory is null || decryptor is null || output is null)
        {
            Console.Error.WriteLine("--snapshot-directory, --external-decryptor, and --output are required.");
            return 2;
        }

        try
        {
            var snapshotRoot = Path.GetFullPath(snapshotDirectory);
            var manifestPath = Path.GetFullPath(snapshotManifest ?? Path.Combine(snapshotRoot, ".wechatvoice", "snapshot-manifest.json"));
            var manifest = await ReadJsonFileAsync<SnapshotManifest>(manifestPath, cancellationToken).ConfigureAwait(false);
            var rawSnapshot = new RawSnapshot(manifest, snapshotRoot);
            var verifiedSnapshot = await new RawSnapshotVerifier().VerifyAsync(rawSnapshot, cancellationToken).ConfigureAwait(false);
            var materialization = await new ExternalDatabaseMaterializer(decryptor).MaterializeAsync(
                verifiedSnapshot,
                new MaterializationOptions(Path.GetFullPath(output), keyFile is null ? null : Path.GetFullPath(keyFile)),
                cancellationToken).ConfigureAwait(false);
            var localWorkspacePath = Path.GetFullPath(workspaceOutput ?? Path.Combine(output, ".wechatvoice", "local-workspace.json"));
            var localWorkspace = await new LocalWorkspaceCreator().CreateAsync(materialization, cancellationToken).ConfigureAwait(false);
            await WriteJsonFileAsync(localWorkspacePath, localWorkspace, cancellationToken).ConfigureAwait(false);
            WriteJson(new WorkspaceMaterializationResult(
                materialization.Result.WorkspaceId,
                materialization.OutputRoot,
                materialization.Result.ManifestPath,
                localWorkspacePath,
                localWorkspace.WorkspaceId,
                localWorkspace.DataSet.DataSetId,
                materialization.Result.Databases.Count));
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Console.Error.WriteLine("Database materialization was cancelled.");
            return 130;
        }
        catch (Exception exception)
        {
            WriteError(exception);
            return 1;
        }
    });
    workspaceCommand.Subcommands.Add(materializeCommand);
    return workspaceCommand;
}

static Command CreateContactCommand()
{
    var contactCommand = new Command("contact", "Discover contacts using stable internal identifiers.");
    var listCommand = new Command("list", "List contacts from a verified data-set adapter.");
    var searchCommand = new Command("search", "Search contacts by username, WeChat ID, remark, or nickname.");
    var listWorkspaceOption = new Option<string>("--workspace") { Description = "Local executable workspace JSON.", Required = true };
    var searchWorkspaceOption = new Option<string>("--workspace") { Description = "Local executable workspace JSON.", Required = true };
    var searchOption = new Option<string>("--query") { Description = "Search text.", Required = true };

    listCommand.Options.Add(listWorkspaceOption);
    listCommand.SetAction(async (parseResult, cancellationToken) =>
    {
        var workspace = parseResult.GetValue(listWorkspaceOption);
        try
        {
            await using var catalog = await OpenCatalogAsync(workspace!, cancellationToken).ConfigureAwait(false);
            var contacts = new List<ContactRecord>();
            await foreach (var contact in catalog.QueryContactsAsync(new ContactQuery(), cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                contacts.Add(contact);
            }

            WriteJson(contacts);
            return 0;
        }
        catch (Exception exception)
        {
            WriteError(exception);
            return 1;
        }
    });

    searchCommand.Options.Add(searchWorkspaceOption);
    searchCommand.Options.Add(searchOption);
    searchCommand.SetAction(async (parseResult, cancellationToken) =>
    {
        var workspace = parseResult.GetValue(searchWorkspaceOption);
        var queryText = parseResult.GetValue(searchOption);
        try
        {
            await using var catalog = await OpenCatalogAsync(workspace!, cancellationToken).ConfigureAwait(false);
            var contacts = new List<ContactRecord>();
            await foreach (var contact in catalog.QueryContactsAsync(new ContactQuery(queryText), cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                contacts.Add(contact);
            }

            WriteJson(contacts);
            return 0;
        }
        catch (Exception exception)
        {
            WriteError(exception);
            return 1;
        }
    });

    contactCommand.Subcommands.Add(listCommand);
    contactCommand.Subcommands.Add(searchCommand);
    return contactCommand;
}

static async Task<IVoiceCatalog> OpenCatalogAsync(string path, CancellationToken cancellationToken)
{
    var verified = await new WorkspaceLoader().LoadVerifiedAsync(path, cancellationToken).ConfigureAwait(false);
    var resolver = new DataSetAdapterResolver(BuiltInAdapters.Create());
    var adapter = resolver.Resolve(verified.DataSet);
    return await adapter.OpenAsync(verified, cancellationToken).ConfigureAwait(false);
}

static VoiceQuery BuildVoiceQuery(string? conversationId, ContactRecord contact, string? directionText, string? fromText, string? toText)
{
    ArgumentNullException.ThrowIfNull(contact);
    if (!string.IsNullOrWhiteSpace(conversationId)
        && !string.Equals(conversationId, contact.ConversationId, StringComparison.Ordinal))
    {
        throw new ArgumentException("--conversation-id conflicts with the selected contact's stable ConversationId.", nameof(conversationId));
    }

    VoiceDirection? direction = null;
    if (!string.IsNullOrWhiteSpace(directionText))
    {
        if (!Enum.TryParse<VoiceDirection>(directionText, true, out var parsedDirection))
        {
            throw new ArgumentException("--direction must be incoming or outgoing.");
        }

        direction = parsedDirection;
    }

    return new VoiceQuery(
        conversationId ?? contact.ConversationId,
        direction,
        ParseUtc(fromText, "--from"),
        ParseUtc(toText, "--to"),
        ContactUsername: contact.Username,
        ContactId: contact.ContactId);
}

static DateTimeOffset? ParseUtc(string? value, string optionName)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return null;
    }

    if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
    {
        throw new ArgumentException($"{optionName} is not a valid UTC date/time.");
    }

    return parsed;
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

static async Task<T> ReadJsonFileAsync<T>(string path, CancellationToken cancellationToken)
{
    var fullPath = Path.GetFullPath(path);
    await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
    var value = await JsonSerializer.DeserializeAsync<T>(stream, CliJson.Options, cancellationToken).ConfigureAwait(false);
    return value ?? throw new InvalidDataException($"The JSON document was empty: '{fullPath}'.");
}

static void WriteJson<T>(T value) =>
    Console.WriteLine(JsonSerializer.Serialize(value, CliJson.Options));

static void WriteError(Exception exception) =>
    Console.Error.WriteLine($"{exception.GetType().Name}: {exception.Message}");

static int GetExportExitCode(VoiceExportManifest manifest)
{
    if (manifest.Failures.Any(static failure => string.Equals(failure.Stage, "query", StringComparison.Ordinal)))
    {
        return 1;
    }

    if (manifest.Entries.Count == 0 && manifest.Failures.Count == 0)
    {
        return 4;
    }

    return manifest.Failures.Count == 0 ? 0 : 3;
}

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

internal sealed record DatasetProbeResult(string OutputPath, string DataSetId, int DatabaseCount, int IssueCount, int AdapterCandidateCount);

internal sealed record WorkspaceCreateResult(string OutputPath, string WorkspaceId, string DataSetId, int DatabaseCount, int IssueCount);

internal sealed record WorkspaceVerifyResult(string WorkspacePath, string WorkspaceId, string DataSetId, int DatabaseCount, DateTimeOffset VerifiedAtUtc);

internal sealed record WorkspaceMaterializationResult(
    string MaterializationWorkspaceId,
    string OutputRoot,
    string MaterializationManifestPath,
    string LocalWorkspacePath,
    string LocalWorkspaceId,
    string DataSetId,
    int DatabaseCount);

internal static class CliJson
{
    internal static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
