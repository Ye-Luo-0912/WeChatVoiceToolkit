namespace WeChatVoice.Tests;

/// <summary>
/// Owns one uniquely-created directory beneath the operating system temp
/// directory. Tests may only clean up that exact directory.
/// </summary>
internal sealed class TestTemporaryDirectory : IDisposable
{
    private readonly string _tempRoot;

    public TestTemporaryDirectory()
    {
        _tempRoot = Path.GetFullPath(Path.GetTempPath());
        RootPath = Path.Combine(
            _tempRoot,
            "WeChatVoiceToolkit.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(RootPath);
    }

    public string RootPath { get; }

    public string GetPath(params string[] segments)
    {
        ArgumentNullException.ThrowIfNull(segments);

        var candidate = Path.GetFullPath(Path.Combine([RootPath, .. segments]));
        var rootWithSeparator = RootPath.EndsWith(Path.DirectorySeparatorChar)
            ? RootPath
            : RootPath + Path.DirectorySeparatorChar;

        if (!candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(candidate, RootPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Test data must remain inside its dedicated temporary directory.");
        }

        return candidate;
    }

    public string CreateDirectory(params string[] segments)
    {
        var path = GetPath(segments);
        Directory.CreateDirectory(path);
        return path;
    }

    public string WriteFile(string relativePath, ReadOnlySpan<byte> contents)
    {
        var path = GetPath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, contents.ToArray());
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(RootPath))
        {
            var rootWithSeparator = _tempRoot.EndsWith(Path.DirectorySeparatorChar)
                ? _tempRoot
                : _tempRoot + Path.DirectorySeparatorChar;
            if (!RootPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Refusing to delete a test directory outside the OS temp directory.");
            }

            Directory.Delete(RootPath, recursive: true);
        }
    }
}
