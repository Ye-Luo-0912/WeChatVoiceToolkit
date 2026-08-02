using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using WeChatVoice.Cli.Services;
using WeChatVoice.Core.Errors;
using WeChatVoice.Core.Models;
using WeChatVoice.Core.Ports;
using WeChatVoice.Infrastructure.Sqlite;
using WeChatVoice.KeyProfileMetadata;
using WeChatVoice.Workflows.Composition;
using WeChatVoice.Workflows.Workflows;

var rootCommand = new RootCommand("Safe, schema-agnostic WeChat voice toolkit foundation.");
rootCommand.Subcommands.Add(CreateDoctorCommand());
rootCommand.Subcommands.Add(CreateSnapshotCommand());
rootCommand.Subcommands.Add(CreateSchemaCommand());
rootCommand.Subcommands.Add(CreateVoiceCommand());
rootCommand.Subcommands.Add(CreateDatasetCommand());
rootCommand.Subcommands.Add(CreateWorkspaceCommand());
rootCommand.Subcommands.Add(CreateMaterializationCommand());
rootCommand.Subcommands.Add(CreateContactCommand());

return rootCommand.Parse(args).Invoke();

// The CLI is a pure command layer: every product flow runs through the shared
// workflows (WeChatVoice.Workflows), never through direct Infrastructure
// composition. Developer diagnostics (schema/dataset probe) still call their
// single probe services directly; they compose nothing.
static WorkflowCompositionRoot CreateRoot(bool allowDevelopmentBroker = false)
    => new(new ConsoleAccountConfirmation(), allowDevelopmentBroker);

static void ReportProgress(OperationProgress progress)
{
    var percent = progress.Stage.PercentComplete is { } complete ? $" {complete:0}%" : string.Empty;
    Console.Error.WriteLine($"stage:{progress.Phase}:{progress.Stage.Id}{percent}");
}

static Command CreateDoctorCommand()
{
    var command = new Command("doctor", "Report installed capabilities and optionally match a verified local workspace.");
    var workspaceOption = new Option<string?>("--workspace")
    {
        Description = "Optional local workspace JSON to verify and probe against registered adapters.",
    };
    command.Options.Add(workspaceOption);
    command.SetAction(async (parseResult, cancellationToken) =>
    {
        try
        {
            var context = new WorkflowContext(CreateRoot().AccountConfirmation, new Progress<OperationProgress>(ReportProgress));
            var result = await CreateRoot().EnvironmentAssessment.RunAsync(
                new EnvironmentAssessmentRequest(parseResult.GetValue(workspaceOption)),
                context,
                cancellationToken).ConfigureAwait(false);
            var report = new DoctorReport(
                System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                Environment.OSVersion.VersionString,
                result.IsWindows,
                result.SupportedProcessNames,
                result.RunningWeChatProcesses,
                new CapabilityReport(
                    RegisteredAdapters: result.RegisteredAdapters,
                    AdapterMatchEvaluated: result.AdapterMatchEvaluated,
                    MatchingAdapters: result.MatchingAdapters,
                    KeyAcquisitionProfiles: result.KeyAcquisitionProfiles,
                    MatchingKeyAcquisitionProfiles: result.MatchingKeyAcquisitionProfiles,
                    MaterializationBackends: ["weixin-windows-4"],
                    BrokerAcquireAndMaterializeAvailable: result.BrokerAcquireAndMaterializeAvailable,
                    OrdinaryCliCanReadProcessMemory: false,
                    AllowsArbitraryProcessMemoryRead: false,
                    HasUserInterface: false));

            WriteJson(report);
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Console.Error.WriteLine("Doctor was cancelled.");
            return 130;
        }
        catch (Exception exception)
        {
            WriteError(exception);
            return 1;
        }
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
            var context = new WorkflowContext(CreateRoot().AccountConfirmation, new Progress<OperationProgress>(ReportProgress));
            var result = await CreateRoot().Snapshot.RunAsync(
                new SnapshotWorkflowRequest(source, output, AllowLiveSource: allowLiveSource, MaxAttempts: maxAttempts),
                context,
                cancellationToken).ConfigureAwait(false);
            WriteJson(result.Manifest);
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
    var maximumResultsOption = new Option<int?>("--maximum-results")
    {
        Description = "Optional global result limit.",
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
    exportCommand.Options.Add(maximumResultsOption);
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
        var maximumResults = parseResult.GetValue(maximumResultsOption);
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
            var context = new WorkflowContext(CreateRoot().AccountConfirmation, new Progress<OperationProgress>(ReportProgress));
            var result = await CreateRoot().VoiceExport.RunAsync(
                new VoiceExportWorkflowRequest(
                    workspacePath,
                    output,
                    ContactUsername: contactUsername,
                    ConversationId: conversationId,
                    Direction: ParseDirection(directionText),
                    From: VoiceQueryBuilder.ParseUtc(fromText, "--from"),
                    To: VoiceQueryBuilder.ParseUtc(toText, "--to"),
                    MaximumResults: maximumResults),
                context,
                cancellationToken).ConfigureAwait(false);
            WriteJson(result.Manifest);
            return GetExportExitCode(result.Manifest);
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
    var scanDeepOption = new Option<bool>("--deep-scan") { Description = "Read and hash complete linked SILK payloads." };
    var scanDurationOption = new Option<bool>("--resolve-durations") { Description = "Decode linked SILK and calculate validated PCM duration." };
    var scanMaximumOption = new Option<int?>("--maximum-results") { Description = "Optional global result limit." };
    scanCommand.Options.Add(scanWorkspaceOption);
    scanCommand.Options.Add(scanContactOption);
    scanCommand.Options.Add(scanDirectionOption);
    scanCommand.Options.Add(scanFromOption);
    scanCommand.Options.Add(scanToOption);
    scanCommand.Options.Add(scanConversationOption);
    scanCommand.Options.Add(scanDeepOption);
    scanCommand.Options.Add(scanDurationOption);
    scanCommand.Options.Add(scanMaximumOption);
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
            var context = new WorkflowContext(CreateRoot().AccountConfirmation, new Progress<OperationProgress>(ReportProgress));
            var result = await CreateRoot().VoiceScan.RunAsync(
                new VoiceScanWorkflowRequest(
                    workspace,
                    ContactUsername: parseResult.GetValue(scanContactOption),
                    ConversationId: parseResult.GetValue(scanConversationOption),
                    Direction: ParseDirection(parseResult.GetValue(scanDirectionOption)),
                    From: VoiceQueryBuilder.ParseUtc(parseResult.GetValue(scanFromOption), "--from"),
                    To: VoiceQueryBuilder.ParseUtc(parseResult.GetValue(scanToOption), "--to"),
                    MaximumResults: parseResult.GetValue(scanMaximumOption),
                    DeepScan: parseResult.GetValue(scanDeepOption),
                    ResolveDurations: parseResult.GetValue(scanDurationOption)),
                context,
                cancellationToken).ConfigureAwait(false);
            WriteJson(result.Report);
            return result.Report.MatchedVoiceCount == 0 ? 4 : 0;
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
            var manifest = await CreateRoot().VoiceExport.RecoverRunAsync(journalPath, cancellationToken).ConfigureAwait(false);
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

static VoiceDirection? ParseDirection(string? directionText)
{
    if (string.IsNullOrWhiteSpace(directionText))
    {
        return null;
    }

    if (!Enum.TryParse<VoiceDirection>(directionText, true, out var parsedDirection))
    {
        throw new ArgumentException("--direction must be incoming or outgoing.");
    }

    return parsedDirection;
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
            var context = new WorkflowContext(CreateRoot().AccountConfirmation, new Progress<OperationProgress>(ReportProgress));
            var result = await CreateRoot().Workspace.CreateAsync(
                new WorkspaceCreateRequest(root, output),
                context,
                cancellationToken).ConfigureAwait(false);
            WriteJson(new WorkspaceCreateResult(
                Path.GetFullPath(output),
                result.Workspace.WorkspaceId,
                result.Workspace.DataSet.DataSetId,
                result.Workspace.DataSet.Databases.Count,
                result.Workspace.Issues.Count));
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
            var context = new WorkflowContext(CreateRoot().AccountConfirmation, new Progress<OperationProgress>(ReportProgress));
            var verified = await CreateRoot().Workspace.VerifyAsync(workspacePath, context, cancellationToken).ConfigureAwait(false);
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
    var backendOption = new Option<string>("--backend")
    {
        Description = "Registered materialization backend. Formal mode defaults to weixin-windows-4.",
        DefaultValueFactory = _ => "weixin-windows-4",
    };
    var decryptorOption = new Option<string?>("--external-decryptor")
    {
        Description = "Development-only external backend executable; requires --allow-untrusted-backend.",
    };
    var allowUntrustedBackendOption = new Option<bool>("--allow-untrusted-backend")
    {
        Description = "Explicitly allow the development-only external backend. It is never a formal backend pin.",
    };
    var allowDevelopmentBrokerOption = new Option<bool>("--allow-development-broker")
    {
        Description = "Accept an unsigned development Key Broker only when it is located in a verified repository build directory.",
    };
    var accountOption = new Option<string?>("--account")
    {
        Description = "Exact stable Weixin account username; doubles as explicit confirmation of the detected account.",
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
    materializeCommand.Options.Add(backendOption);
    materializeCommand.Options.Add(decryptorOption);
    materializeCommand.Options.Add(allowUntrustedBackendOption);
    materializeCommand.Options.Add(allowDevelopmentBrokerOption);
    materializeCommand.Options.Add(accountOption);
    materializeCommand.Options.Add(materializedOutputOption);
    materializeCommand.Options.Add(workspaceOutputOption);
    materializeCommand.SetAction(async (parseResult, cancellationToken) =>
    {
        var snapshotDirectory = parseResult.GetValue(snapshotDirectoryOption);
        var snapshotManifest = parseResult.GetValue(snapshotManifestOption);
        var backendId = parseResult.GetValue(backendOption);
        var decryptor = parseResult.GetValue(decryptorOption);
        var allowUntrustedBackend = parseResult.GetValue(allowUntrustedBackendOption);
        var allowDevelopmentBroker = parseResult.GetValue(allowDevelopmentBrokerOption);
        var requestedAccount = parseResult.GetValue(accountOption);
        var output = parseResult.GetValue(materializedOutputOption);
        var workspaceOutput = parseResult.GetValue(workspaceOutputOption);
        if (snapshotDirectory is null || backendId is null || output is null)
        {
            Console.Error.WriteLine("--snapshot-directory, --backend, and --output are required.");
            return 2;
        }

        if (allowDevelopmentBroker)
        {
            Console.Error.WriteLine("警告：使用未签名的开发构建 Key Broker，仅供开发调试，禁止用于正式发布。");
        }

        try
        {
            var root = CreateRoot(allowDevelopmentBroker);
            var context = new WorkflowContext(root.AccountConfirmation, new Progress<OperationProgress>(ReportProgress));
            var result = await root.Materialization.RunAsync(
                new MaterializationWorkflowRequest(
                    snapshotDirectory,
                    snapshotManifest,
                    backendId,
                    decryptor,
                    AllowUntrustedBackend: allowUntrustedBackend,
                    RequestedAccountId: requestedAccount,
                    OutputDirectory: output,
                    WorkspaceOutputPath: workspaceOutput),
                context,
                cancellationToken).ConfigureAwait(false);
            if (result.ProfileId is not null)
            {
                WriteJson(new BrokerWorkspaceMaterializationResult(
                    result.ProfileId,
                    result.MaterializationId!,
                    result.LocalWorkspacePath,
                    result.Workspace.Workspace.WorkspaceId,
                    result.Workspace.DataSet.DataSetId,
                    result.Workspace.DataSet.Databases.Count));
            }
            else
            {
                WriteJson(new WorkspaceMaterializationResult(
                    result.MaterializationId!,
                    Path.GetFullPath(output),
                    Path.Combine(Path.GetFullPath(output), ".wechatvoice", "materialization-manifest.json"),
                    result.LocalWorkspacePath,
                    result.Workspace.Workspace.WorkspaceId,
                    result.Workspace.DataSet.DataSetId,
                    result.Workspace.DataSet.Databases.Count));
            }

            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Console.Error.WriteLine("Database materialization was cancelled.");
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
    workspaceCommand.Subcommands.Add(materializeCommand);
    workspaceCommand.Subcommands.Add(CreateMaterializationRecoveryCommand("adopt", "Adopt a committed materialization whose workspace JSON was not committed."));
    return workspaceCommand;
}

static Command CreateMaterializationCommand()
{
    var command = new Command("materialization", "Recover a committed materialization without decrypting the databases again.");
    command.Subcommands.Add(CreateMaterializationRecoveryCommand("recover", "Recover a committed materialization and create or verify its workspace JSON."));
    return command;
}

static Command CreateMaterializationRecoveryCommand(string name, string description)
{
    var command = new Command(name, description);
    var outputOption = new Option<string>("--output")
    {
        Description = "Existing materialized database output root.",
        Required = true,
    };
    var workspaceOutputOption = new Option<string?>("--workspace-output")
    {
        Description = "Workspace JSON path; defaults to <output>.workspace.json beside the output root.",
    };
    var accountOption = new Option<string?>("--account")
    {
        Description = "Optional exact stable account username for legacy manifests without AccountId.",
    };
    command.Options.Add(outputOption);
    command.Options.Add(workspaceOutputOption);
    command.Options.Add(accountOption);
    command.SetAction(async (parseResult, cancellationToken) =>
    {
        var output = parseResult.GetValue(outputOption);
        var workspaceOutput = parseResult.GetValue(workspaceOutputOption);
        var account = parseResult.GetValue(accountOption);
        if (output is null)
        {
            Console.Error.WriteLine("--output is required.");
            return 2;
        }

        try
        {
            var root = CreateRoot();
            var context = new WorkflowContext(root.AccountConfirmation, new Progress<OperationProgress>(ReportProgress));
            var verified = await root.Workspace.RecoverMaterializationAsync(
                new MaterializationRecoveryRequest(output, workspaceOutput, account),
                context,
                cancellationToken).ConfigureAwait(false);
            WriteJson(new WorkspaceRecoveryResult(
                Path.GetFullPath(output),
                Path.GetFullPath(workspaceOutput ?? Path.Combine(
                    Path.GetDirectoryName(Path.GetFullPath(output))!,
                    Path.GetFileName(Path.GetFullPath(output)) + ".workspace.json")),
                verified.Workspace.WorkspaceId,
                verified.DataSet.DataSetId,
                verified.DataSet.Databases.Count,
                verified.VerifiedAtUtc));
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Console.Error.WriteLine("Materialization recovery was cancelled.");
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
    return command;
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
            var context = new WorkflowContext(CreateRoot().AccountConfirmation, new Progress<OperationProgress>(ReportProgress));
            var result = await CreateRoot().ContactDiscovery.RunAsync(
                new ContactDiscoveryRequest(workspace!),
                context,
                cancellationToken).ConfigureAwait(false);
            WriteJson(result.Contacts);
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Console.Error.WriteLine("Contact listing was cancelled.");
            return 130;
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
            var context = new WorkflowContext(CreateRoot().AccountConfirmation, new Progress<OperationProgress>(ReportProgress));
            var result = await CreateRoot().ContactDiscovery.RunAsync(
                new ContactDiscoveryRequest(workspace!, SearchTerm: queryText),
                context,
                cancellationToken).ConfigureAwait(false);
            WriteJson(result.Contacts);
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Console.Error.WriteLine("Contact search was cancelled.");
            return 130;
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

static async Task<T> ReadJsonFileAsync<T>(string path, CancellationToken cancellationToken)
{
    var fullPath = Path.GetFullPath(path);
    await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
    var value = await JsonSerializer.DeserializeAsync<T>(stream, CliJson.Options, cancellationToken).ConfigureAwait(false);
    return value ?? throw new InvalidDataException($"The JSON document was empty: '{fullPath}'.");
}

static async Task WriteJsonFileAsync<T>(string outputPath, T value, CancellationToken cancellationToken)
{
    ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

    var fullOutputPath = Path.GetFullPath(outputPath);
    var directory = Path.GetDirectoryName(fullOutputPath)
        ?? throw new ArgumentException("The output path must include a directory.", nameof(outputPath));
    Directory.CreateDirectory(directory);
    var temporaryPath = fullOutputPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
    try
    {
        await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
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

static void WriteError(Exception exception)
{
    // Stable error codes stay machine-readable; the presentation layer owns
    // the localized text. This is the same boundary a UI host uses.
    switch (exception)
    {
        case AppFailureException appException:
            WriteLocalized(appException.Code);
            return;
        case BrokerTransportException brokerException:
            Console.Error.WriteLine($"[{brokerException.Code}] {brokerException.Message}");
            return;
        case NoMatchingDataSetAdapterException:
            WriteLocalized(ErrorCode.UnsupportedSchema);
            return;
        default:
            Console.Error.WriteLine($"{exception.GetType().Name}: {exception.Message}");
            return;
    }

    static void WriteLocalized(ErrorCode code)
    {
        var zh = ErrorMessagesZhHans.Get(code);
        Console.Error.WriteLine($"[{code}] {zh.Message}{(zh.SuggestedAction is null ? string.Empty : "（" + zh.SuggestedAction + "）")}");
    }
}

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
    IReadOnlyList<WeChatVoice.Windows.WeChatProcessInfo> RunningWeChatProcesses,
    CapabilityReport Capabilities);

internal sealed record CapabilityReport(
    IReadOnlyList<string> RegisteredAdapters,
    bool AdapterMatchEvaluated,
    IReadOnlyList<string> MatchingAdapters,
    IReadOnlyList<KeyProfileMetadata> KeyAcquisitionProfiles,
    IReadOnlyList<string> MatchingKeyAcquisitionProfiles,
    IReadOnlyList<string> MaterializationBackends,
    bool BrokerAcquireAndMaterializeAvailable,
    bool OrdinaryCliCanReadProcessMemory,
    bool AllowsArbitraryProcessMemoryRead,
    bool HasUserInterface);

internal sealed record SchemaProbeResult(string OutputPath, int ObjectCount);

internal sealed record DatasetProbeResult(string OutputPath, string DataSetId, int DatabaseCount, int IssueCount, int AdapterCandidateCount);

internal sealed record WorkspaceCreateResult(string OutputPath, string WorkspaceId, string DataSetId, int DatabaseCount, int IssueCount);

internal sealed record WorkspaceVerifyResult(string WorkspacePath, string WorkspaceId, string DataSetId, int DatabaseCount, DateTimeOffset VerifiedAtUtc);

internal sealed record WorkspaceRecoveryResult(
    string OutputRoot,
    string WorkspacePath,
    string WorkspaceId,
    string DataSetId,
    int DatabaseCount,
    DateTimeOffset VerifiedAtUtc);

internal sealed record WorkspaceMaterializationResult(
    string MaterializationWorkspaceId,
    string OutputRoot,
    string MaterializationManifestPath,
    string LocalWorkspacePath,
    string LocalWorkspaceId,
    string DataSetId,
    int DatabaseCount);

internal sealed record BrokerWorkspaceMaterializationResult(
    string ProfileId,
    string MaterializationId,
    string LocalWorkspacePath,
    string WorkspaceId,
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

/// <summary>
/// Presentation-layer mapping from stable error codes to zh-Hans guidance.
/// The codes themselves are the machine contract; this table is display only.
/// </summary>
internal static class ErrorMessagesZhHans
{
    internal static (string Message, string? SuggestedAction) Get(ErrorCode code) => code switch
    {
        ErrorCode.WeixinNotRunning => ("未检测到正在运行的受支持 Weixin 进程", "start-weixin"),
        ErrorCode.UnsupportedWeixinVersion => ("当前 Weixin 版本不受支持（仅支持 4.1.11.55）", "use-supported-version"),
        ErrorCode.ProcessIdentityMismatch => ("Weixin 进程身份证据与 Profile 不匹配", "restart-weixin-and-retry"),
        ErrorCode.SnapshotInvalid => ("快照校验失败", "re-snapshot"),
        ErrorCode.SnapshotInconsistent => ("快照内容在验证期间发生变化", "re-snapshot"),
        ErrorCode.KeyCandidateNotFound => ("未能为每个数据库组校验出密钥", "restart-weixin-and-retry"),
        ErrorCode.DatabaseGroupUncovered => ("密钥采集未覆盖全部必需的源数据库", "re-snapshot"),
        ErrorCode.WorkerBundleUntrusted => ("SQLCipher Worker 或 Key Broker 未通过信任校验", "reinstall-package"),
        ErrorCode.WorkerFailed => ("SQLCipher Worker 处理失败", "retry-materialization"),
        ErrorCode.MaterializationInvalid => ("物料化输出未通过独立校验", "retry-materialization"),
        ErrorCode.WorkspaceInvalid => ("本地 Workspace 无效或与确认账号不一致", "re-materialize"),
        ErrorCode.UnsupportedSchema => ("未找到支持该数据库结构的已验证适配器", "use-supported-schema"),
        ErrorCode.ContactNotFound => ("未找到请求的稳定联系人", "choose-contact"),
        ErrorCode.ExportPartialFailure => ("导出完成但存在逐条失败", "review-failures"),
        ErrorCode.AccountConfirmationRequired => ("账号身份仅为候选，需要明确确认", "confirm-account"),
        ErrorCode.UacElevationRejected => ("UAC 管理员授权被拒绝", "retry-materialization"),
        _ => ("操作失败", null),
    };
}
