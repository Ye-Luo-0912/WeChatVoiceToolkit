using System.CommandLine;
using WeChatVoice.Cli.Services;
using WeChatVoice.Core.Models;
using WeChatVoice.Workflows.Composition;

namespace WeChatVoice.Cli;

internal static partial class CliApplication
{
    public static int Run(string[] args)
    {
        var rootCommand = new RootCommand("Safe, schema-agnostic WeChat voice toolkit foundation.");
        rootCommand.Subcommands.Add(CreateDoctorCommand());
        rootCommand.Subcommands.Add(CreateSnapshotCommand());
        rootCommand.Subcommands.Add(CreateSchemaCommand());
        rootCommand.Subcommands.Add(CreateVoiceCommand());
        rootCommand.Subcommands.Add(CreateDatasetCommand());
        rootCommand.Subcommands.Add(CreateWorkspaceCommand());
        rootCommand.Subcommands.Add(CreateMaterializationCommand());
        rootCommand.Subcommands.Add(CreateContactCommand());
        rootCommand.Subcommands.Add(CreateStorageCommand());

        return rootCommand.Parse(args).Invoke();
    }

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

}
