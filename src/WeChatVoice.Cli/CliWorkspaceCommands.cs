using System.CommandLine;
using WeChatVoice.Core.Models;
using WeChatVoice.Workflows.Workflows;

namespace WeChatVoice.Cli;

internal static partial class CliApplication
{
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
                await using var composition = CreateRoot();
                var context = new WorkflowContext(composition.AccountConfirmation, new Progress<OperationProgress>(ReportProgress));
                var result = await composition.Workspace.CreateAsync(
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
                await using var composition = CreateRoot();
                var context = new WorkflowContext(composition.AccountConfirmation, new Progress<OperationProgress>(ReportProgress));
                var verified = await composition.Workspace.VerifyAsync(workspacePath, context, cancellationToken).ConfigureAwait(false);
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
                await using var root = CreateRoot(allowDevelopmentBroker);
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
        workspaceCommand.Subcommands.Add(CreateWorkspaceRepairCommand());
        return workspaceCommand;
    }

    static Command CreateWorkspaceRepairCommand()
    {
        var command = new Command("repair", "Recreate a missing Workspace JSON after a Completed materialization without re-decrypting databases.");
        var outputOption = new Option<string>("--output")
        {
            Description = "Completed materialized database output root.",
            Required = true,
        };
        var workspaceOutputOption = new Option<string?>("--workspace-output")
        {
            Description = "Workspace JSON path; defaults to <output>.workspace.json beside the output root.",
        };
        var accountOption = new Option<string?>("--account")
        {
            Description = "Exact stable account username when the manifest contains only a path candidate.",
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
                await using var root = CreateRoot();
                var context = new WorkflowContext(root.AccountConfirmation, new Progress<OperationProgress>(ReportProgress));
                var verified = await root.Workspace.RepairMaterializationAsync(
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
                Console.Error.WriteLine("Workspace repair was cancelled.");
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
                await using var root = CreateRoot();
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

}
