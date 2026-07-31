using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using WeChatVoice.Core.Models;
using WeChatVoice.Core.Ports;
using WeChatVoice.Infrastructure.Serialization;
using WeChatVoice.Infrastructure.Sqlite;

namespace WeChatVoice.Infrastructure.Materialization;

/// <summary>
/// Runs a fixed external decryptor protocol. The executable is configured by
/// the host, while arguments and output validation remain fixed and bounded.
/// </summary>
public sealed class ExternalDatabaseMaterializer : IDatabaseMaterializer
{
    private const int OutputLimit = 64 * 1024;
    private readonly string _executablePath;

    public ExternalDatabaseMaterializer(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        _executablePath = Path.GetFullPath(executablePath);
    }

    public string Id => "external-decryptor-v1";

    public async Task<DecryptedWorkspace> MaterializeAsync(
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

        if (!Directory.Exists(snapshot.SnapshotDirectory))
        {
            throw new DirectoryNotFoundException($"The raw snapshot directory was not found: '{snapshot.SnapshotDirectory}'.");
        }

        if (Directory.Exists(options.OutputDirectory) && Directory.EnumerateFileSystemEntries(options.OutputDirectory).Any())
        {
            throw new IOException("The materialization output directory must be new or empty.");
        }

        var parent = Path.GetDirectoryName(options.OutputDirectory)
            ?? throw new ArgumentException("The materialization output directory must have a parent.", nameof(options));
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, $".{Path.GetFileName(options.OutputDirectory)}.{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(staging);
        try
        {
            var result = await RunDecryptorAsync(snapshot.SnapshotDirectory, staging, options.KeyFile, cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                throw new DatabaseMaterializationException(result.ExitCode, result.StandardOutput, result.StandardError, "The external decryptor returned a non-zero exit code.");
            }

            var databases = await ValidateOutputAsync(staging, cancellationToken).ConfigureAwait(false);
            if (databases.Count == 0)
            {
                throw new DatabaseMaterializationException(result.ExitCode, result.StandardOutput, result.StandardError, "The external decryptor produced no SQLite databases.");
            }

            if (Directory.Exists(options.OutputDirectory))
            {
                Directory.Delete(options.OutputDirectory, recursive: true);
            }

            Directory.Move(staging, options.OutputDirectory);
            var moved = databases.Select(database => database with { DatabasePath = Path.Combine(options.OutputDirectory, Path.GetRelativePath(staging, database.DatabasePath)) }).ToArray();
            var workspaceId = "materialized-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshot.SnapshotId + "|" + Id))).ToLowerInvariant()[..16];
            return new DecryptedWorkspace(workspaceId, snapshot.SnapshotId, Id, "1", moved);
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                TryDeleteDirectory(staging);
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

    private static async Task<IReadOnlyList<MaterializedDatabase>> ValidateOutputAsync(string outputRoot, CancellationToken cancellationToken)
    {
        WindowsSqliteProvider.EnsureInitialized();
        var databases = new List<MaterializedDatabase>();
        foreach (var path in Directory.EnumerateFiles(outputRoot, "*.db", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
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

            var role = ClassifyRole(Path.GetFileName(path), out var shard);
            databases.Add(new MaterializedDatabase(role, shard, path, await FileHashing.ComputeSha256Async(path, cancellationToken).ConfigureAwait(false), new FileInfo(path).Length));
        }

        return databases;
    }

    private static string ClassifyRole(string fileName, out int? shard)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        foreach (var prefix in new[] { "message", "media" })
        {
            if (stem.StartsWith(prefix + "_", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(stem[(prefix.Length + 1)..], out var number)
                && number >= 0)
            {
                shard = number;
                return prefix;
            }
        }

        shard = null;
        return fileName.Contains("contact", StringComparison.OrdinalIgnoreCase) ? "contact" : "unknown";
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

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

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
