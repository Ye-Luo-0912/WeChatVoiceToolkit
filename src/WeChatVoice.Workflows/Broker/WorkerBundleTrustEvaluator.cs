using System.Text.Json;
using WeChatVoice.Infrastructure.Serialization;

namespace WeChatVoice.Workflows.Broker;

/// <summary>Read-only preflight equivalent of the Key Broker worker check.</summary>
public static class WorkerBundleTrustEvaluator
{
    public static async Task<WorkerBundleTrustResult> VerifyAsync(string directory, CancellationToken cancellationToken)
    {
        try
        {
            var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var worker = Path.Combine(directory, "WeChatVoice.SqlCipherWorker.exe");
            var manifestPath = Path.Combine(directory, "WeChatVoice.SqlCipherWorker.bundle.json");
            if (!IsSafeFile(worker, root) || !IsSafeFile(manifestPath, root))
            {
                return WorkerBundleTrustResult.Deny("worker-bundle-unavailable");
            }

            await using var stream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var json = document.RootElement;
            if (!await MatchesAsync(worker, json, "workerExeSha256", cancellationToken).ConfigureAwait(false))
            {
                return WorkerBundleTrustResult.Deny("worker-hash-mismatch");
            }

            foreach (var (pathProperty, hashProperty) in new[]
                     {
                         ("depsFile", "depsSha256"),
                         ("runtimeConfigFile", "runtimeConfigSha256"),
                         ("nativeSqlCipherFile", "nativeSqlCipherSha256"),
                         ("providerFile", "providerSha256"),
                     })
            {
                if (!json.TryGetProperty(pathProperty, out var pathValue) || !json.TryGetProperty(hashProperty, out var hashValue))
                {
                    return WorkerBundleTrustResult.Deny("worker-bundle-entry-missing");
                }

                var relative = pathValue.GetString();
                var expected = hashValue.GetString();
                if (string.IsNullOrWhiteSpace(relative) || string.IsNullOrWhiteSpace(expected) || Path.IsPathRooted(relative))
                {
                    return WorkerBundleTrustResult.Deny("worker-bundle-entry-invalid");
                }

                var path = Path.GetFullPath(Path.Combine(directory, relative));
                if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !IsSafeFile(path, root)
                    || !string.Equals(await FileHashing.ComputeSha256Async(path, cancellationToken).ConfigureAwait(false), expected, StringComparison.OrdinalIgnoreCase))
                {
                    return WorkerBundleTrustResult.Deny("worker-bundle-sidecar-mismatch");
                }
            }

            if (!json.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
            {
                return WorkerBundleTrustResult.Deny("worker-bundle-closure-missing");
            }

            if (!await VerifyCompleteClosureAsync(directory, root, files, cancellationToken).ConfigureAwait(false))
            {
                return WorkerBundleTrustResult.Deny("worker-bundle-closure-mismatch");
            }

            return WorkerBundleTrustResult.Ok();
        }
        catch (JsonException)
        {
            return WorkerBundleTrustResult.Deny("worker-bundle-invalid");
        }
        catch (IOException)
        {
            return WorkerBundleTrustResult.Deny("worker-bundle-unavailable");
        }
        catch (UnauthorizedAccessException)
        {
            return WorkerBundleTrustResult.Deny("worker-bundle-unavailable");
        }
        catch (InvalidOperationException)
        {
            return WorkerBundleTrustResult.Deny("worker-bundle-invalid");
        }
        catch (InvalidDataException)
        {
            return WorkerBundleTrustResult.Deny("worker-bundle-invalid");
        }
        catch (ArgumentException)
        {
            return WorkerBundleTrustResult.Deny("worker-bundle-invalid");
        }
    }

    private static async Task<bool> MatchesAsync(string path, JsonElement json, string hashProperty, CancellationToken cancellationToken)
        => json.TryGetProperty(hashProperty, out var expected)
           && !string.IsNullOrWhiteSpace(expected.GetString())
           && string.Equals(await FileHashing.ComputeSha256Async(path, cancellationToken).ConfigureAwait(false), expected.GetString(), StringComparison.OrdinalIgnoreCase);

    private static bool IsSafeFile(string path, string root)
        => File.Exists(path)
           && Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase)
           && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;

    private static async Task<bool> VerifyCompleteClosureAsync(
        string directory,
        string root,
        JsonElement files,
        CancellationToken cancellationToken)
    {
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in files.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("relativePath", out var relativeValue)
                || !entry.TryGetProperty("sha256", out var hashValue)
                || !entry.TryGetProperty("byteLength", out var lengthValue)
                || relativeValue.GetString() is not { Length: > 0 } relative
                || hashValue.GetString() is not { Length: > 0 } expectedHash
                || !lengthValue.TryGetInt64(out var expectedLength)
                || expectedLength < 0
                || Path.IsPathRooted(relative))
            {
                return false;
            }

            var normalized = NormalizeRelative(relative);
            if (!expected.Add(normalized))
            {
                return false;
            }

            var path = Path.GetFullPath(Path.Combine(directory, relative));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !IsSafeFile(path, root))
            {
                return false;
            }

            var info = new FileInfo(path);
            if (info.Length != expectedLength
                || !string.Equals(await FileHashing.ComputeSha256Async(path, cancellationToken).ConfigureAwait(false), expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (expected.Count == 0)
        {
            return false;
        }

        var actual = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(path => !IsPackageMetadata(Path.GetRelativePath(directory, path)))
            .Select(path =>
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("The Worker bundle contains a reparse-point file.");
                }

                return NormalizeRelative(Path.GetRelativePath(directory, path));
            })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return actual.SetEquals(expected);
    }

    private static bool IsPackageMetadata(string relativePath)
        => Path.GetFileName(relativePath).Equals("WeChatVoice.KeyBroker.bundle.json", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(relativePath).Equals("WeChatVoice.SqlCipherWorker.bundle.json", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(relativePath).Equals("package-manifest.json", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(relativePath).Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(relativePath).Equals("sbom.spdx.json", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeRelative(string path)
    {
        var normalized = path.Replace('\\', '/');
        if (Path.IsPathRooted(path)
            || normalized.Equals(".", StringComparison.Ordinal)
            || normalized.StartsWith("../", StringComparison.Ordinal)
            || normalized.Contains("/../", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The Worker bundle contains a path outside the install directory.");
        }

        return normalized;
    }
}
