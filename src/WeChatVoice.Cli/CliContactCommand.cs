using System.CommandLine;
using WeChatVoice.Core.Models;
using WeChatVoice.Workflows.Workflows;

namespace WeChatVoice.Cli;

internal static partial class CliApplication
{
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
                await using var root = CreateRoot();
                var context = new WorkflowContext(root.AccountConfirmation, new Progress<OperationProgress>(ReportProgress));
                var result = await root.ContactDiscovery.RunAsync(
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
                await using var root = CreateRoot();
                var context = new WorkflowContext(root.AccountConfirmation, new Progress<OperationProgress>(ReportProgress));
                var result = await root.ContactDiscovery.RunAsync(
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

}
