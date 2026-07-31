using WeChatVoice.SqlCipherWorker;

return await SqlCipherWorkerHost.RunAsync(args, CancellationToken.None).ConfigureAwait(false);
