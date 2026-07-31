using WeChatVoice.KeyBroker;

return await BrokerHost.RunAsync(Console.In, Console.Out, CancellationToken.None).ConfigureAwait(false);
