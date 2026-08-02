namespace WeChatVoice.Infrastructure.Sqlite;

public static class WorkspacePathSafety
{
    public static void EnsureNoReparsePoints(string root)
    {
        var fullRoot = Path.GetFullPath(root);
        if (!Directory.Exists(fullRoot))
        {
            throw new DirectoryNotFoundException($"Workspace root does not exist: '{fullRoot}'.");
        }

        if ((File.GetAttributes(fullRoot) & FileAttributes.ReparsePoint) != 0)
        {
            throw new WorkspaceVerificationException($"Reparse points are not allowed in a local workspace: '{fullRoot}'.");
        }

        var pending = new Stack<string>();
        pending.Push(fullRoot);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.OrdinalIgnoreCase))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new WorkspaceVerificationException($"Reparse points are not allowed in a local workspace: '{entry}'.");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                }
            }
        }
    }
}
