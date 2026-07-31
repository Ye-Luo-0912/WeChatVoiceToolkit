using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using WeChatVoice.Core.Models;
using WeChatVoice.Infrastructure.Serialization;
using WeChatVoice.Infrastructure.Snapshots;
using WeChatVoice.Infrastructure.Sqlite;
using WeChatVoice.KeyAcquisition.Models;
using WeChatVoice.KeyAcquisition.Ports;
using WeChatVoice.Windows;

namespace WeChatVoice.KeyBroker;

/// <summary>
/// Runs the bundled SQLCipher compatibility worker once per verified database
/// group. The key crosses only the private child-process stdin pipe and is
/// cleared from the envelope as soon as it has been written.
/// </summary>
internal sealed class SqlCipherEphemeralDatabaseMaterializer(string? workerPath = null) : IEphemeralDatabaseMaterializer
{
    private const int WorkerKeySize = 32;
    private const int ProtocolHeaderSize = 5;
    private const int OutputLimit = 64 * 1024;
    private readonly string workerPath = Path.GetFullPath(workerPath ?? Path.Combine(AppContext.BaseDirectory, "WeChatVoice.SqlCipherWorker.dll"));

    public string BackendId => "sqlcipher-e_sqlcipher-worker";

    public string EncryptionProfileId => "weixin-windows-4.1.11.55-sqlcipher4-page-hmac-v1";

    public async Task<VerifiedMaterialization> MaterializeAsync(
        VerifiedRawSnapshot snapshot,
        VerifiedKeyAcquisition acquisition,
        MaterializationOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(acquisition);
        ArgumentNullException.ThrowIfNull(options);
        if (!string.Equals(acquisition.ProfileId, EncryptionProfileId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The SQLCipher worker received an unexpected key-extraction Profile.");
        }

        if (!File.Exists(workerPath))
        {
            throw new FileNotFoundException("The bundled SQLCipher worker was not found.", workerPath);
        }

        if (Directory.Exists(options.OutputDirectory) || File.Exists(options.OutputDirectory))
        {
            throw new IOException("The materialization output target must not already exist.");
        }

        var backendSha256 = await FileHashing.ComputeSha256Async(workerPath, cancellationToken).ConfigureAwait(false);
        var sourceRoot = Path.GetFullPath(snapshot.Snapshot.SnapshotDirectory);
        var parent = Path.GetDirectoryName(options.OutputDirectory)
            ?? throw new ArgumentException("The materialization output directory must have a parent.", nameof(options));
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, $".{Path.GetFileName(options.OutputDirectory)}.{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(staging);
        Exception? primaryFailure = null;
        try
        {
            var targetByPath = snapshot.Snapshot.Manifest.Files
                .Where(static file => file.RelativePath.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(static file => NormalizeRelative(file.RelativePath), StringComparer.OrdinalIgnoreCase);
            var materialized = new List<MaterializedDatabase>(acquisition.Bindings.Count);
            foreach (var binding in acquisition.Bindings)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!targetByPath.ContainsKey(NormalizeRelative(binding.RelativeDatabasePath)))
                {
                    throw new InvalidDataException("A key binding referenced a database outside the verified Snapshot.");
                }

                var inputPath = CombineUnderRoot(sourceRoot, binding.RelativeDatabasePath);
                if (!File.Exists(inputPath))
                {
                    throw new FileNotFoundException("A bound database disappeared from the verified Snapshot.", inputPath);
                }

                var outputRelative = NormalizeRelative(Path.Combine("databases", binding.RelativeDatabasePath));
                var outputPath = CombineUnderRoot(staging, outputRelative);
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(options.BackendTimeout);
                await RunWorkerAsync(inputPath, outputPath, binding.ProtectedKeyMaterial, timeout.Token).ConfigureAwait(false);
                await ValidatePlaintextSqliteAsync(outputPath, timeout.Token).ConfigureAwait(false);
                var info = new FileInfo(outputPath);
                var hash = await FileHashing.ComputeSha256Async(outputPath, timeout.Token).ConfigureAwait(false);
                var schema = await new SqliteSchemaInspector().InspectAsync(
                    outputPath,
                    new SchemaInspectionOptions(IncludeLocalPaths: false, PrecomputedSha256: hash, PrecomputedByteLength: info.Length),
                    timeout.Token).ConfigureAwait(false);
                materialized.Add(new MaterializedDatabase(
                    NormalizeRelative(binding.RelativeDatabasePath),
                    binding.DatabaseGroupFingerprint,
                    outputRelative,
                    Classify(binding.RelativeDatabasePath),
                    binding.ShardNumber,
                    hash,
                    info.Length,
                    schema.SchemaFingerprint ?? string.Empty));
            }

            var files = await EnumerateFilesAsync(staging, cancellationToken).ConfigureAwait(false);
            var workspaceId = ComputeWorkspaceId(snapshot.Snapshot.SnapshotId, backendSha256, materialized);
            var manifestPath = Path.Combine(staging, ".wechatvoice", "materialization-manifest.json");
            Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
            var manifest = new MaterializationManifest(
                workspaceId,
                snapshot.Snapshot.SnapshotId,
                BackendId,
                "e_sqlcipher-2.1.11-worker-v1",
                backendSha256,
                materialized.OrderBy(static item => item.SourceRelativePath, StringComparer.OrdinalIgnoreCase).ToArray(),
                files);
            await using (var stream = new FileStream(manifestPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await JsonSerializer.SerializeAsync(stream, manifest, InfrastructureJson.Indented, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            Directory.Move(staging, options.OutputDirectory);
            var movedManifestPath = Path.Combine(options.OutputDirectory, ".wechatvoice", "materialization-manifest.json");
            return new VerifiedMaterialization(new MaterializationResult(
                workspaceId,
                snapshot.Snapshot.SnapshotId,
                BackendId,
                "e_sqlcipher-2.1.11-worker-v1",
                backendSha256,
                options.OutputDirectory,
                materialized,
                files,
                movedManifestPath), DateTimeOffset.UtcNow);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            throw;
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                try
                {
                    Directory.Delete(staging, recursive: true);
                }
                catch (Exception cleanupException) when (cleanupException is IOException or UnauthorizedAccessException)
                {
                    if (primaryFailure is null)
                    {
                        throw new IOException("SQLCipher materialization staging cleanup failed.", cleanupException);
                    }

                    throw new AggregateException(primaryFailure, cleanupException);
                }
            }
        }
    }

    private async Task RunWorkerAsync(string inputPath, string outputPath, SensitiveBuffer key, CancellationToken cancellationToken)
    {
        if (key.Length != WorkerKeySize)
        {
            throw new InvalidDataException("The validated database key has an unsupported length.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(workerPath);
        startInfo.ArgumentList.Add("--input");
        startInfo.ArgumentList.Add(inputPath);
        startInfo.ArgumentList.Add("--output");
        startInfo.ArgumentList.Add(outputPath);
        using var process = Process.Start(startInfo) ?? throw new IOException("The SQLCipher worker could not be started.");
        var envelope = new byte[ProtocolHeaderSize + WorkerKeySize];
        try
        {
            "WCV1"u8.CopyTo(envelope);
            envelope[4] = WorkerKeySize;
            key.CopyTo(envelope.AsSpan(ProtocolHeaderSize));
            await process.StandardInput.BaseStream.WriteAsync(envelope, cancellationToken).ConfigureAwait(false);
            await process.StandardInput.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();
            var stdoutTask = ReadBoundedAsync(process.StandardOutput, cancellationToken);
            var stderrTask = ReadBoundedAsync(process.StandardError, cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new IOException("The SQLCipher worker rejected the verified key or database.");
            }
        }
        catch
        {
            TryKill(process);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(envelope);
        }
    }

    private static async Task ValidatePlaintextSqliteAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var header = new byte[16];
        var read = 0;
        while (read < header.Length)
        {
            var count = await stream.ReadAsync(header.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            read += count;
        }

        if (read != header.Length || !header.AsSpan().SequenceEqual("SQLite format 3\0"u8))
        {
            throw new InvalidDataException("The SQLCipher worker did not produce a plaintext SQLite database.");
        }

        WindowsSqliteProvider.EnsureInitialized();
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA query_only = ON;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        command.CommandText = "PRAGMA quick_check;";
        var result = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The SQLCipher worker output failed SQLite quick_check.");
        }
    }

    private static async Task<IReadOnlyList<MaterializationFile>> EnumerateFilesAsync(string root, CancellationToken cancellationToken)
    {
        var files = new List<MaterializationFile>();
        foreach (var path in EnumerateRegularFilesStrict(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = NormalizeRelative(Path.GetRelativePath(root, path));
            var info = new FileInfo(path);
            files.Add(new MaterializationFile(relative, await FileHashing.ComputeSha256Async(path, cancellationToken).ConfigureAwait(false), info.Length));
        }

        return files.OrderBy(static item => item.OutputRelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IEnumerable<string> EnumerateRegularFilesStrict(string root)
    {
        var rootAttributes = File.GetAttributes(root);
        if ((rootAttributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("The materialization staging root cannot be a reparse point.");
        }

        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("The materialization staging output contains a reparse point.");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                }
                else
                {
                    yield return entry;
                }
            }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var total = 0;
        while (true)
        {
            var read = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return string.Empty;
            }

            total += read;
            if (total > OutputLimit)
            {
                // Continue draining without retaining unbounded child output.
                total = OutputLimit;
            }
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static string ComputeWorkspaceId(string snapshotId, string backendSha256, IEnumerable<MaterializedDatabase> databases)
    {
        var canonical = string.Join('\n', databases.OrderBy(static item => item.OutputRelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(item => string.Join('|', item.OutputRelativePath, item.Sha256, item.ByteLength.ToString(System.Globalization.CultureInfo.InvariantCulture), item.SchemaFingerprint)));
        return "materialized-" + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join('|', snapshotId, backendSha256, canonical)))).ToLowerInvariant();
    }

    private static string Classify(string path)
    {
        var lower = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        if (lower.StartsWith("message_", StringComparison.Ordinal))
        {
            return "message";
        }

        if (lower.StartsWith("media_", StringComparison.Ordinal))
        {
            return "media";
        }

        return lower.Contains("contact", StringComparison.Ordinal) || lower.Contains("friend", StringComparison.Ordinal)
            ? "contact"
            : "unknown";
    }

    private static string NormalizeRelative(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
        {
            throw new InvalidDataException("A database path was not relative.");
        }

        var normalized = SnapshotFileCopier.NormalizeRelativePath(path);
        if (normalized.Equals(".", StringComparison.Ordinal) || normalized.StartsWith("../", StringComparison.Ordinal) || normalized.Contains("/../", StringComparison.Ordinal))
        {
            throw new InvalidDataException("A database path escaped its root.");
        }

        return normalized;
    }

    private static string CombineUnderRoot(string root, string relativePath)
    {
        var normalized = NormalizeRelative(relativePath);
        var fullRoot = Path.GetFullPath(root);
        var candidate = Path.GetFullPath(Path.Combine(fullRoot, normalized.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A database path escaped its root.");
        }

        return candidate;
    }
}
