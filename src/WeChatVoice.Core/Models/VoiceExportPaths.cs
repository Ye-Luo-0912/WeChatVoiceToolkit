namespace WeChatVoice.Core.Models;

/// <summary>
/// Store-owned physical destinations and portable manifest paths for one voice export.
/// The store is responsible for making the parent directories available.
/// </summary>
public sealed record VoiceExportPaths
{
    /// <summary>
    /// Creates paths whose manifest names are the output file names. Stores that
    /// preserve a directory hierarchy should use the four-argument overload.
    /// </summary>
    public VoiceExportPaths(string OriginalFilePath, string DecodedFilePath)
        : this(
            OriginalFilePath,
            DecodedFilePath,
            Path.GetFileName(OriginalFilePath),
            Path.GetFileName(DecodedFilePath))
    {
    }

    public VoiceExportPaths(
        string OriginalFilePath,
        string DecodedFilePath,
        string OriginalManifestPath,
        string DecodedManifestPath)
    {
        this.OriginalFilePath = NormalizeFilePath(OriginalFilePath, nameof(OriginalFilePath), ".silk");
        this.DecodedFilePath = NormalizeFilePath(DecodedFilePath, nameof(DecodedFilePath), ".wav");
        this.OriginalManifestPath = NormalizeManifestPath(OriginalManifestPath, nameof(OriginalManifestPath));
        this.DecodedManifestPath = NormalizeManifestPath(DecodedManifestPath, nameof(DecodedManifestPath));

        if (string.Equals(this.OriginalFilePath, this.DecodedFilePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Original and decoded files must have different destinations.");
        }
    }

    public string OriginalFilePath { get; }

    public string DecodedFilePath { get; }

    public string OriginalManifestPath { get; }

    public string DecodedManifestPath { get; }

    private static string NormalizeFilePath(string value, string parameterName, string requiredExtension)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A file path is required.", parameterName);
        }

        if (!Path.IsPathFullyQualified(value))
        {
            throw new ArgumentException("The file path must be fully qualified.", parameterName);
        }

        if (!string.Equals(Path.GetExtension(value), requiredExtension, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"The file path must use the {requiredExtension} extension.", parameterName);
        }

        return Path.GetFullPath(value);
    }

    private static string NormalizeManifestPath(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A manifest path is required.", parameterName);
        }

        if (Path.IsPathRooted(value))
        {
            throw new ArgumentException("Manifest paths must be relative to the export root.", parameterName);
        }

        var segments = value.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(static segment => segment is "." or ".."))
        {
            throw new ArgumentException("Manifest paths cannot traverse outside the export root.", parameterName);
        }

        return string.Join('/', segments);
    }
}
