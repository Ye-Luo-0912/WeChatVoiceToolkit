using System.CommandLine;
using WeChatVoice.Core.Models;
using WeChatVoice.Workflows.Workflows;

namespace WeChatVoice.Cli;

internal static partial class CliApplication
{
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
                await using var root = CreateRoot();
                var context = new WorkflowContext(root.AccountConfirmation, new Progress<OperationProgress>(ReportProgress));
                var result = await root.EnvironmentAssessment.RunAsync(
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

}
