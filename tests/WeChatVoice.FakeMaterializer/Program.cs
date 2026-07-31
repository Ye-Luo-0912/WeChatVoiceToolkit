using System.Text.Json;
using System.Text.Json.Serialization;
using WeChatVoice.Core.Models;

namespace WeChatVoice.FakeMaterializer;

public sealed class FakeMaterializerMarker;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static int Main(string[] args)
    {
        if (!TryParse(args, out var inputRoot, out var outputRoot))
        {
            return 2;
        }

        var modePath = Path.Combine(inputRoot!, ".fake-materializer-mode");
        var mode = File.Exists(modePath) ? File.ReadAllText(modePath).Trim() : "success";
        if (string.Equals(mode, "exit", StringComparison.Ordinal))
        {
            Console.Error.WriteLine("key=00112233 salt=aabbccdd path=C:\\sensitive\\database.db");
            return 17;
        }

        if (string.Equals(mode, "hang", StringComparison.Ordinal))
        {
            Thread.Sleep(TimeSpan.FromMinutes(5));
            return 0;
        }

        Directory.CreateDirectory(outputRoot!);
        var mappings = new List<MaterializationOutputDatabase>();
        foreach (var sourcePath in Directory.EnumerateFiles(inputRoot!, "*.db", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
        {
            var relative = Path.GetRelativePath(inputRoot!, sourcePath).Replace('\\', '/');
            if (string.Equals(mode, "missing", StringComparison.Ordinal) && relative.EndsWith("media_0.db", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var destination = Path.Combine(outputRoot!, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (string.Equals(mode, "invalid", StringComparison.Ordinal) && relative.EndsWith("media_0.db", StringComparison.OrdinalIgnoreCase))
            {
                File.WriteAllBytes(destination, [1, 2, 3, 4]);
            }
            else
            {
                File.Copy(sourcePath, destination);
            }

            mappings.Add(new MaterializationOutputDatabase(relative, relative, MaterializationDatabaseStatus.CopiedAsPlaintext));
        }

        if (string.Equals(mode, "duplicate", StringComparison.Ordinal) && mappings.Count > 0)
        {
            mappings.Add(mappings[0]);
        }

        if (string.Equals(mode, "unknown-source", StringComparison.Ordinal))
        {
            mappings.Add(new MaterializationOutputDatabase("unknown.db", "unknown.db", MaterializationDatabaseStatus.Materialized));
        }

        if (string.Equals(mode, "extra", StringComparison.Ordinal))
        {
            File.WriteAllBytes(Path.Combine(outputRoot!, "extra.db"), "SQLite format 3\0"u8.ToArray());
        }

        var manifestDirectory = Path.Combine(outputRoot!, ".wechatvoice");
        Directory.CreateDirectory(manifestDirectory);
        var manifest = new MaterializationOutputManifest(MaterializationOutputManifest.CurrentFormatVersion, mappings);
        File.WriteAllText(Path.Combine(manifestDirectory, "materialization-output.json"), JsonSerializer.Serialize(manifest, JsonOptions));
        return 0;
    }

    private static bool TryParse(string[] args, out string? inputRoot, out string? outputRoot)
    {
        inputRoot = null;
        outputRoot = null;
        if (args.Length != 4)
        {
            return false;
        }

        for (var index = 0; index < args.Length; index += 2)
        {
            switch (args[index])
            {
                case "--input-root":
                    inputRoot = args[index + 1];
                    break;
                case "--output-root":
                    outputRoot = args[index + 1];
                    break;
                default:
                    return false;
            }
        }

        return inputRoot is not null && outputRoot is not null;
    }
}
