namespace WeChatVoice.Core.Models;

/// <summary>
/// Canonical names for the public and private products of an export run.
/// Keeping these names in Core prevents the CLI, Desktop, verification, and
/// persistence layers from silently selecting different manifest types.
/// </summary>
public static class ExportManifestLayout
{
    public const string PrivateManifestFileName = "manifest.private.json";
    public const string PortableManifestFileName = "dataset.manifest.json";
    public const string PortableCsvFileName = "dataset.csv";

    /// <summary>
    /// Kept only for reading exports produced before the public/private split.
    /// New writes must never create this ambiguous name.
    /// </summary>
    public const string LegacyPortableManifestFileName = "latest.manifest.json";

    public static string RunPrivateManifestFileName(string runId)
        => runId + ".manifest.private.json";

    public static string RunPortableManifestFileName(string runId)
        => runId + ".dataset.manifest.json";

    public static string RunPortableCsvFileName(string runId)
        => runId + ".dataset.csv";
}
