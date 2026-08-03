using System.CommandLine;
using WeChatVoice.Core.Models;
using WeChatVoice.Workflows.Workflows;

namespace WeChatVoice.Cli;

internal static partial class CliApplication
{
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
                await using var root = CreateRoot();
                var context = new WorkflowContext(root.AccountConfirmation, new Progress<OperationProgress>(ReportProgress));
                var result = await root.Snapshot.RunAsync(
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

}
