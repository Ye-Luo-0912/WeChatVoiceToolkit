using WeChatVoice.Core.Models;

namespace WeChatVoice.Workflows.Workflows;

/// <summary>
/// Lists or searches contacts from a verified workspace through the exact
/// adapter. The catalog is disposed by this workflow; hosts never hold it.
/// </summary>
public sealed class ContactDiscoveryWorkflow(
    Workspaces.VoiceCatalogOpener opener) : IContactDiscoveryWorkflow
{
    public async Task<ContactDiscoveryResult> RunAsync(
        ContactDiscoveryRequest request,
        WorkflowContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        if (!context.TryStart())
        {
            throw new InvalidOperationException("The workflow state machine is not idle.");
        }

        try
        {
            context.Report(OperationPhase.ContactDiscovery, OperationStageIds.LoadingWorkspace);
            await using var session = await opener.OpenAsync(request.WorkspacePath, cancellationToken).ConfigureAwait(false);
            var query = new ContactQuery(Username: request.Username, SearchTerm: request.SearchTerm);
            var contacts = new List<ContactRecord>();
            context.Report(OperationPhase.ContactDiscovery, OperationStageIds.ResolvingContact);
            await foreach (var contact in session.Catalog.QueryContactsAsync(query, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                contacts.Add(contact);
            }

            context.StateMachine.TryComplete();
            context.Report(OperationPhase.ContactDiscovery, OperationStageIds.Completing);
            return new ContactDiscoveryResult(contacts, session.Workspace, Path.GetFullPath(request.WorkspacePath));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            context.StateMachine.TryCancel();
            throw;
        }
        catch
        {
            context.StateMachine.TryFail();
            throw;
        }
    }
}
