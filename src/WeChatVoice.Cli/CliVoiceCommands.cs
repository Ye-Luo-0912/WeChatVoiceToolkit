using System.CommandLine;
using WeChatVoice.Core.Errors;
using WeChatVoice.Core.Models;
using WeChatVoice.Workflows.Workflows;

namespace WeChatVoice.Cli;

internal static partial class CliApplication
{
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
                await using var root = CreateRoot();
                var direction = ParseDirection(directionText) ?? VoiceDirection.Incoming;
                var scanContext = new WorkflowContext(root.AccountConfirmation, new Progress<OperationProgress>(ReportProgress));
                var scanResult = await root.VoiceScan.RunAsync(
                    new VoiceScanWorkflowRequest(
                        workspacePath,
                        ContactUsername: contactUsername,
                        ConversationId: conversationId,
                        Direction: direction,
                        From: VoiceQueryBuilder.ParseUtc(fromText, "--from"),
                        To: VoiceQueryBuilder.ParseUtc(toText, "--to"),
                        MaximumResults: maximumResults),
                    scanContext,
                    cancellationToken).ConfigureAwait(false);
                var prepared = scanResult.Selection
                    ?? throw new AppFailureException(ErrorCode.WorkflowFailed, "The scan did not produce a prepared export selection.");
                var exportContext = new WorkflowContext(root.AccountConfirmation, new Progress<OperationProgress>(ReportProgress));
                var result = await root.VoiceExport.RunAsync(
                    prepared,
                    new ExportDestination(output),
                    exportContext,
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
                await using var root = CreateRoot();
                var context = new WorkflowContext(root.AccountConfirmation, new Progress<OperationProgress>(ReportProgress));
                var result = await root.VoiceScan.RunAsync(
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
                await using var root = CreateRoot();
                var manifest = await root.VoiceExport.RecoverRunAsync(journalPath, cancellationToken).ConfigureAwait(false);
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

        var verifyExportCommand = new Command("verify", "Verify export manifests, Journal commit, CSV, index, and SILK artifacts without modifying them.");
        var verifyOutputOption = new Option<string>("--output") { Description = "Export root directory.", Required = true };
        var verifyRunIdOption = new Option<string?>("--run-id") { Description = "Optional run ID; defaults to the latest private manifest." };
        verifyExportCommand.Options.Add(verifyOutputOption);
        verifyExportCommand.Options.Add(verifyRunIdOption);
        verifyExportCommand.SetAction(async (parseResult, cancellationToken) =>
        {
            var output = parseResult.GetValue(verifyOutputOption);
            var runId = parseResult.GetValue(verifyRunIdOption);
            try
            {
                await using var root = CreateRoot();
                var result = await root.VoiceExport.VerifyAsync(
                    new ExportVerificationRequest(output!, runId),
                    new WorkflowContext(root.AccountConfirmation, new Progress<OperationProgress>(ReportProgress)),
                    cancellationToken).ConfigureAwait(false);
                WriteJson(result);
                return result.IsValid ? 0 : 1;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Console.Error.WriteLine("Export verification was cancelled.");
                return 130;
            }
            catch (Exception exception)
            {
                WriteError(exception);
                return 1;
            }
        });

        var repairExportCommand = new Command("repair", "Regenerate only export manifests, CSV files, and the artifact index after verifying SILK files.");
        var repairOutputOption = new Option<string>("--output") { Description = "Export root directory.", Required = true };
        var repairRunIdOption = new Option<string?>("--run-id") { Description = "Optional run ID; defaults to the latest committed Journal." };
        repairExportCommand.Options.Add(repairOutputOption);
        repairExportCommand.Options.Add(repairRunIdOption);
        repairExportCommand.SetAction(async (parseResult, cancellationToken) =>
        {
            var output = parseResult.GetValue(repairOutputOption);
            var runId = parseResult.GetValue(repairRunIdOption);
            try
            {
                await using var root = CreateRoot();
                var result = await root.VoiceExport.RepairAsync(
                    new ExportRepairRequest(output!, runId),
                    new WorkflowContext(root.AccountConfirmation, new Progress<OperationProgress>(ReportProgress)),
                    cancellationToken).ConfigureAwait(false);
                WriteJson(result);
                return result.Verification.IsValid ? 0 : 1;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Console.Error.WriteLine("Export repair was cancelled.");
                return 130;
            }
            catch (Exception exception)
            {
                WriteError(exception);
                return 1;
            }
        });
        exportCommand.Subcommands.Add(verifyExportCommand);
        exportCommand.Subcommands.Add(repairExportCommand);
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

}
