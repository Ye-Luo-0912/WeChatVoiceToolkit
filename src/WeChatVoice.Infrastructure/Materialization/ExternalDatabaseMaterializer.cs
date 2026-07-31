using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using WeChatVoice.Core.Models;
using WeChatVoice.Core.Ports;
using WeChatVoice.Infrastructure.Serialization;
using WeChatVoice.Infrastructure.Snapshots;
using WeChatVoice.Infrastructure.Sqlite;

namespace WeChatVoice.Infrastructure.Materialization;

/// <summary>
/// Runs a fixed external decryptor protocol and produces a verified ordinary
/// SQLite workspace. The executable is configured by the host; arguments,
/// source verification, output mapping, and acceptance rules remain fixed.
/// </summary>
public sealed class ExternalDatabaseMaterializer : IDatabaseMaterializer
{
    private const int OutputLimit = 64 * 1024;
    private readonly string _executablePath;
    private readonly string _backendVersion;

    public ExternalDatabaseMaterializer(string executablePath, string backendVersion = "1")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(backendVersion);
        _executablePath = Path.GetFullPath(executablePath);
        _backendVersion = backendVersion;
    }

    public string Id => "external-decryptor-v1";

    public async Task<MaterializationResult> MaterializeAsync(
        RawSnapshot snapshot,
        MaterializationOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(_executablePath))
        {
            throw new FileNotFoundException("The configured external decryptor was not found.", _executablePath);
        }

        var sourceRoot = Path.GetFullPath(snapshot.SnapshotDirectory);
        if (!Directory.Exists(sourceRoot))
        {
            throw new DirectoryNotFoundException($"The raw snapshot directory was not found: '{sourceRoot}'.");
        }

        await ValidateRawSnapshotAsync(snapshot, cancellationToken).ConfigureAwait(false);
        var sourceProbe = await new DataSetProbeService().ProbeAsync(
            sourceRoot,
            new DataSetProbeOptions(IncludeLocalPaths: true),
            cancellationToken).ConfigureAwait(false);
        if (sourceProbe.DataSet.Databases.Count == 0)
        {
            throw new DatabaseMaterializationException(null, null, null, "The raw snapshot contains no database artifacts to materialize.");
        }

        var backendSha256 = await FileHashing.ComputeSha256Async(_executablePath, cancellationToken).ConfigureAwait(false);

        if (Directory.Exists(options.OutputDirectory) || File.Exists(options.OutputDirectory))
        {
            throw new IOException("The materialization output target must not already exist.");
        }

        var parent = Path.GetDirectoryName(options.OutputDirectory)
            ?? throw new ArgumentException("The materialization output directory must have a parent.", nameof(options));
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, $".{Path.GetFileName(options.OutputDirectory)}.{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(staging);
        Exception? primaryFailure = null;
        try
        {
            var result = await RunDecryptorAsync(sourceRoot, staging, options.KeyFile, cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                throw new DatabaseMaterializationException(result.ExitCode, result.StandardOutput, result.StandardError, "The external decryptor returned a non-zero exit code.");
            }

            var validation = await ValidateOutputAsync(staging, sourceProbe.DataSet.Databases, cancellationToken).ConfigureAwait(false);
            if (validation.Databases.Any(static database => database.Status == MaterializationDatabaseStatus.Failed))
            {
                throw new DatabaseMaterializationException(result.ExitCode, result.StandardOutput, result.StandardError, "The external decryptor did not materialize every required source database.");
            }

            var workspaceId = ComputeWorkspaceId(snapshot.SnapshotId, backendSha256, _backendVersion, validation.Databases);
            var manifestPath = Path.Combine(staging, ".wechatvoice", "materialization-manifest.json");
            Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
            var manifest = new MaterializationManifest(
                workspaceId,
                snapshot.SnapshotId,
                Id,
                _backendVersion,
                backendSha256,
                validation.Databases,
                validation.Files);
            await using (var manifestStream = new FileStream(manifestPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await JsonSerializer.SerializeAsync(manifestStream, manifest, InfrastructureJson.Indented, cancellationToken).ConfigureAwait(false);
                await manifestStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            Directory.Move(staging, options.OutputDirectory);
            var movedManifestPath = Path.Combine(options.OutputDirectory, ".wechatvoice", "materialization-manifest.json");
            return new MaterializationResult(
                workspaceId,
                snapshot.SnapshotId,
                Id,
                _backendVersion,
                backendSha256,
                options.OutputDirectory,
                validation.Databases,
                validation.Files,
                movedManifestPath);
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
                        throw new DatabaseMaterializationException(null, null, null, $"Materialization staging cleanup failed: {cleanupException.Message}");
                    }

                    throw new AggregateException(primaryFailure, cleanupException);
                }
            }
        }
    }

    private async Task<DecryptorResult> RunDecryptorAsync(string inputRoot, string outputRoot, string? keyFile, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _executablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("--input-root");
        startInfo.ArgumentList.Add(inputRoot);
        startInfo.ArgumentList.Add("--output-root");
        startInfo.ArgumentList.Add(outputRoot);
        if (keyFile is not null)
        {
            startInfo.ArgumentList.Add("--key-file");
            startInfo.ArgumentList.Add(keyFile);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new DatabaseMaterializationException(null, null, null, "The external decryptor could not be started.");
        }

        var stdoutTask = ReadBoundedAsync(process.StandardOutput, cancellationToken);
        var stderrTask = ReadBoundedAsync(process.StandardError, cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            return new DecryptorResult(process.ExitCode, stdoutTask.Result, stderrTask.Result);
        }
        catch
        {
            TryKill(process);
            throw;
        }
    }

    private static async Task<OutputValidation> ValidateOutputAsync(
        string outputRoot,
        IReadOnlyList<DatabaseArtifact> sourceDatabases,
        CancellationToken cancellationToken)
    {
        var outputFiles = EnumerateRegularFilesStrict(outputRoot).ToArray();
        var files = new List<MaterializationFile>();
        foreach (var path in outputFiles.Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = SnapshotFileCopier.NormalizeRelativePath(Path.GetRelativePath(outputRoot, path));
            var info = new FileInfo(path);
            files.Add(new MaterializationFile(relative, await FileHashing.ComputeSha256Async(path, cancellationToken).ConfigureAwait(false), info.Length));
        }

        var databases = new List<MaterializedDatabase>();
        var outputDatabases = outputFiles
            .Where(static path => string.Equals(Path.GetExtension(path), ".db", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var usedSource = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in outputDatabases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ValidateSqliteAsync(path, cancellationToken).ConfigureAwait(false);
            var relative = SnapshotFileCopier.NormalizeRelativePath(Path.GetRelativePath(outputRoot, path));
            var (role, shard) = ClassifyRole(Path.GetFileName(path));
            var source = sourceDatabases.FirstOrDefault(item =>
                !usedSource.Contains(item.DatabasePath)
                && string.Equals(item.DatabasePath, relative, StringComparison.OrdinalIgnoreCase))
                ?? sourceDatabases.FirstOrDefault(item =>
                    !usedSource.Contains(item.DatabasePath)
                    && string.Equals(Path.GetFileName(item.DatabasePath), Path.GetFileName(path), StringComparison.OrdinalIgnoreCase)
                    && string.Equals(item.LogicalRole, role, StringComparison.OrdinalIgnoreCase)
                    && item.ShardNumber == shard);
            if (source is null)
            {
                continue;
            }

            usedSource.Add(source.DatabasePath);
            var info = new FileInfo(path);
            var hash = await FileHashing.ComputeSha256Async(path, cancellationToken).ConfigureAwait(false);
            var schema = await new SqliteSchemaInspector().InspectAsync(path, new SchemaInspectionOptions(IncludeLocalPaths: false, PrecomputedSha256: hash, PrecomputedByteLength: info.Length), cancellationToken).ConfigureAwait(false);
            databases.Add(new MaterializedDatabase(
                source.DatabasePath,
                source.DatabaseGroupFingerprint ?? string.Empty,
                relative,
                role,
                shard,
                hash,
                info.Length,
                schema.SchemaFingerprint ?? string.Empty));
        }

        foreach (var source in sourceDatabases.Where(item => !usedSource.Contains(item.DatabasePath)))
        {
            databases.Add(new MaterializedDatabase(
                source.DatabasePath,
                source.DatabaseGroupFingerprint ?? string.Empty,
                string.Empty,
                source.LogicalRole,
                source.ShardNumber,
                string.Empty,
                0,
                string.Empty,
                MaterializationDatabaseStatus.Failed,
                "The source database was not materialized."));
        }

        var requiredRoles = sourceDatabases.Where(static item => item.LogicalRole is "message" or "media" or "contact")
            .Select(static item => (item.LogicalRole, item.ShardNumber))
            .ToHashSet();
        var materializedRoles = databases.Where(static item => item.Status == MaterializationDatabaseStatus.Materialized)
            .Select(static item => (item.LogicalRole, item.ShardNumber))
            .ToHashSet();
        if (requiredRoles.Except(materializedRoles).Any())
        {
            throw new DatabaseMaterializationException(null, null, null, "The materializer output is missing one or more required message/media/contact databases.");
        }

        return new OutputValidation(databases.OrderBy(static item => item.SourceRelativePath, StringComparer.OrdinalIgnoreCase).ToArray(), files);
    }

    private static async Task ValidateSqliteAsync(string path, CancellationToken cancellationToken)
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
            throw new DatabaseMaterializationException(null, null, null, $"Materializer output is not a plain SQLite database: '{Path.GetFileName(path)}'.");
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
        var quickCheck = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
        if (!string.Equals(quickCheck, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new DatabaseMaterializationException(null, null, null, $"SQLite quick_check failed for '{Path.GetFileName(path)}': {quickCheck ?? "unknown"}.");
        }
    }

    private static IEnumerable<string> EnumerateRegularFilesStrict(string root)
    {
        var attributes = File.GetAttributes(root);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new DatabaseMaterializationException(null, null, null, "Materializer output root cannot be a reparse point.");
        }

        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
            {
                var entryAttributes = File.GetAttributes(entry);
                if ((entryAttributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new DatabaseMaterializationException(null, null, null, $"Materializer output contains a reparse point: '{entry}'.");
                }

                if ((entryAttributes & FileAttributes.Directory) != 0)
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

    private static async Task ValidateRawSnapshotAsync(RawSnapshot snapshot, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(snapshot.SnapshotDirectory);
        var expected = snapshot.Manifest.Files.ToDictionary(static file => file.RelativePath.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase);
        if (expected.Count == 0)
        {
            throw new DatabaseMaterializationException(null, null, null, "The raw snapshot manifest contains no files to verify.");
        }

        var actualPaths = EnumerateRegularFilesStrict(root)
            .Where(path => !IsInternalMetadataPath(root, path))
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!actualPaths.SetEquals(expected.Keys))
        {
            throw new DatabaseMaterializationException(null, null, null, "The raw snapshot file set differs from its manifest; materialization was refused.");
        }

        foreach (var pair in expected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = CombineUnderRoot(root, pair.Key);
            var info = new FileInfo(path);
            var hash = await FileHashing.ComputeSha256Async(path, cancellationToken).ConfigureAwait(false);
            if (info.Length != pair.Value.ByteLength || !string.Equals(hash, pair.Value.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new DatabaseMaterializationException(null, null, null, $"The raw snapshot file failed manifest verification: '{pair.Key}'.");
            }
        }
    }

    private static string ComputeWorkspaceId(string snapshotId, string backendSha256, string backendVersion, IEnumerable<MaterializedDatabase> databases)
    {
        var canonical = string.Join('\n', databases.OrderBy(static item => item.OutputRelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(item => string.Join('|', item.OutputRelativePath, item.Sha256, item.ByteLength.ToString(System.Globalization.CultureInfo.InvariantCulture), item.SchemaFingerprint)));
        var bytes = Encoding.UTF8.GetBytes(string.Join('|', snapshotId, backendSha256, backendVersion, canonical));
        return "materialized-" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static bool IsInternalMetadataPath(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        return relative.Equals(".wechatvoice", StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith(".wechatvoice/", StringComparison.OrdinalIgnoreCase);
    }

    private static string CombineUnderRoot(string root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new DatabaseMaterializationException(null, null, null, "The raw snapshot manifest contains an absolute path.");
        }

        var candidate = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new DatabaseMaterializationException(null, null, null, "The raw snapshot manifest contains a path outside its root.");
        }

        return candidate;
    }

    private static (string Role, int? Shard) ClassifyRole(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        foreach (var prefix in new[] { "message", "media" })
        {
            if (stem.StartsWith(prefix + "_", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(stem[(prefix.Length + 1)..], out var number)
                && number >= 0)
            {
                return (prefix, number);
            }
        }

        return (fileName.Contains("contact", StringComparison.OrdinalIgnoreCase) ? "contact" : "unknown", null);
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var buffer = new char[4096];
        while (true)
        {
            var read = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (builder.Length < OutputLimit)
            {
                builder.Append(buffer, 0, Math.Min(read, OutputLimit - builder.Length));
            }
        }

        return builder.Length == OutputLimit ? builder.ToString() + "…" : builder.ToString();
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

    private sealed record OutputValidation(IReadOnlyList<MaterializedDatabase> Databases, IReadOnlyList<MaterializationFile> Files);

    private sealed record DecryptorResult(int ExitCode, string StandardOutput, string StandardError);
}

public sealed class DatabaseMaterializationException : IOException
{
    public DatabaseMaterializationException(int? ExitCode, string? StandardOutput, string? StandardError, string message)
        : base(message)
    {
        this.ExitCode = ExitCode;
        this.StandardOutput = StandardOutput;
        this.StandardError = StandardError;
    }

    public int? ExitCode { get; }

    public string? StandardOutput { get; }

    public string? StandardError { get; }
}
