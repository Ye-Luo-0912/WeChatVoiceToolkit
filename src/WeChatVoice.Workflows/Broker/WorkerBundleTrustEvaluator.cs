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
    }

    private static async Task<bool> MatchesAsync(string path, JsonElement json, string hashProperty, CancellationToken cancellationToken)
        => json.TryGetProperty(hashProperty, out var expected)
           && !string.IsNullOrWhiteSpace(expected.GetString())
           && string.Equals(await FileHashing.ComputeSha256Async(path, cancellationToken).ConfigureAwait(false), expected.GetString(), StringComparison.OrdinalIgnoreCase);

    private static bool IsSafeFile(string path, string root)
        => File.Exists(path)
           && Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase)
           && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;
}
