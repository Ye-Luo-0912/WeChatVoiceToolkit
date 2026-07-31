using WeChatVoice.KeyBroker;

if ((args.Length is not (8 or 9)) || !string.Equals(args[0], "--pipe-token", StringComparison.Ordinal) ||
    !string.Equals(args[2], "--snapshot-manifest", StringComparison.Ordinal) ||
    !string.Equals(args[4], "--output-root", StringComparison.Ordinal) ||
    !string.Equals(args[6], "--workspace-output", StringComparison.Ordinal))
{
    return 2;
}

var allowExperimentalProfile = args.Length == 9 && string.Equals(args[8], "--allow-experimental-profile", StringComparison.Ordinal);
if (args.Length == 9 && !allowExperimentalProfile)
{
    return 2;
}

try
{
    return await BrokerPipeServer.RunAsync(
        args[1],
        Path.GetFullPath(args[3]),
        Path.GetFullPath(args[5]),
        Path.GetFullPath(args[7]),
        CancellationToken.None,
        allowExperimentalProfile).ConfigureAwait(false);
}
catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or OperationCanceledException)
{
    return 2;
}
