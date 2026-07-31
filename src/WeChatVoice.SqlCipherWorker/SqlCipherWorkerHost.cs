using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace WeChatVoice.SqlCipherWorker;

/// <summary>
/// Compatibility worker for a separately hosted SQLCipher runtime. The worker
/// accepts one fixed input/output pair and receives the key only as bounded
/// stdin bytes; it never writes or prints key material.
/// </summary>
public static class SqlCipherWorkerHost
{
    private const int KeySize = 32;
    private const int MaximumPathLength = 32 * 1024;
    private const string SqlCipher3Page4096ProfileId = "weixin-windows-4.sqlcipher3-page4096-hmac-sha1-v1";
    private const string SqlCipher4Page4096ProfileId = "weixin-windows-4.sqlcipher4-page4096-hmac-sha512-v1";
    private static readonly byte[] ProtocolMagic = "WCV1"u8.ToArray();

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        try
        {
            var (inputPath, outputPath, encryptionProfileId) = ParseArguments(args);
            var key = await ReadKeyAsync(Console.OpenStandardInput(), cancellationToken).ConfigureAwait(false);
            try
            {
                await MaterializeAsync(inputPath, outputPath, encryptionProfileId, key, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }

            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 130;
        }
        catch (SqlCipherWorkerSqliteException exception)
        {
            await Console.Error.WriteLineAsync($"sqlcipher_worker_failed:{exception.Stage}_{exception.SqliteErrorCode}").ConfigureAwait(false);
            return 1;
        }
        catch (SqliteException exception)
        {
            await Console.Error.WriteLineAsync($"sqlcipher_worker_failed:sqlite_{exception.SqliteErrorCode}").ConfigureAwait(false);
            return 1;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            // Keep diagnostics deliberately generic. Never include exception
            // text from a provider that might echo a key or SQL statement.
            await Console.Error.WriteLineAsync($"sqlcipher_worker_failed:{exception.GetType().Name}").ConfigureAwait(false);
            return 1;
        }
    }

    private static async Task MaterializeAsync(
        string inputPath,
        string outputPath,
        string encryptionProfileId,
        byte[] key,
        CancellationToken cancellationToken)
    {
        if (File.Exists(outputPath) || Directory.Exists(outputPath))
        {
            throw new IOException("The SQLCipher output already exists.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlcipher());
        SQLitePCL.raw.FreezeProvider();
        var stagedInputPath = Path.Combine(Path.GetTempPath(), $"wechatvoice-sqlcipher-{Guid.NewGuid():N}.db");
        var stage = "open";
        var outputValidated = false;
        try
        {
            // Never open or mutate the raw snapshot in write mode. SQLCipher's
            // export implementation requires a writable main connection, so
            // use a private copy as the transaction workspace.
            File.Copy(inputPath, stagedInputPath, overwrite: false);
            foreach (var sidecar in new[] { "-wal", "-shm" })
            {
                var sourceSidecar = inputPath + sidecar;
                if (File.Exists(sourceSidecar))
                {
                    File.Copy(sourceSidecar, stagedInputPath + sidecar, overwrite: false);
                }
            }
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = stagedInputPath,
                Mode = SqliteOpenMode.ReadWrite,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
            }.ToString();
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            stage = "key";
            await ApplyRawKeyAsync(connection, key, cancellationToken).ConfigureAwait(false);
            stage = "compatibility";
            await ApplyEncryptionProfileAsync(connection, encryptionProfileId, cancellationToken).ConfigureAwait(false);
            stage = "quick_check";
            var quickCheck = await ScalarAsync(connection, "PRAGMA quick_check;", cancellationToken).ConfigureAwait(false);
            if (!string.Equals(quickCheck, "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("SQLCipher quick_check did not pass.");
            }

            stage = "create_output";
            using (File.Create(outputPath))
            {
            }
            stage = "attach";
            await AttachPlaintextAsync(connection, outputPath, cancellationToken).ConfigureAwait(false);
            try
            {
                stage = "export";
                await ExecuteAsync(connection, "SELECT sqlcipher_export('plaintext');", cancellationToken).ConfigureAwait(false);
                stage = "plaintext_quick_check";
                var plaintextQuickCheck = await ScalarAsync(connection, "PRAGMA plaintext.quick_check;", cancellationToken).ConfigureAwait(false);
                if (!string.Equals(plaintextQuickCheck, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("The plaintext destination failed quick_check.");
                }
            }
            finally
            {
                stage = "detach";
                await ExecuteAsync(connection, "DETACH DATABASE plaintext;", cancellationToken).ConfigureAwait(false);
            }

            stage = "validate_output";
            ValidatePlaintextHeader(outputPath);
            outputValidated = true;
        }
        catch (SqliteException exception)
        {
            throw new SqlCipherWorkerSqliteException(stage, exception.SqliteErrorCode, exception);
        }
        catch (InvalidDataException exception)
        {
            throw new SqlCipherWorkerSqliteException(stage, -1, exception);
        }
        finally
        {
            TryDelete(stagedInputPath);
            TryDelete(stagedInputPath + "-wal");
            TryDelete(stagedInputPath + "-shm");
            if (!outputValidated)
            {
                TryDelete(outputPath);
            }
        }
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ApplyRawKeyAsync(SqliteConnection connection, ReadOnlyMemory<byte> key, CancellationToken cancellationToken)
    {
        var keyHex = Convert.ToHexString(key.Span);
        var sql = $"PRAGMA key = \"x'{keyHex}'\";";
        try
        {
            await ExecuteAsync(connection, sql, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ZeroString(sql);
            ZeroString(keyHex);
        }
    }

    private static async Task ApplyEncryptionProfileAsync(
        SqliteConnection connection,
        string encryptionProfileId,
        CancellationToken cancellationToken)
    {
        var compatibility = encryptionProfileId switch
        {
            SqlCipher3Page4096ProfileId => 3,
            SqlCipher4Page4096ProfileId => 4,
            _ => throw new InvalidDataException("The SQLCipher encryption Profile is not supported."),
        };
        await ExecuteAsync(connection, $"PRAGMA cipher_compatibility = {compatibility};", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "PRAGMA cipher_page_size = 4096;", cancellationToken).ConfigureAwait(false);
    }

    private static void ZeroString(string value)
    {
        var writable = MemoryMarshal.CreateSpan(ref MemoryMarshal.GetReference(value.AsSpan()), value.Length);
        CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(writable));
    }

    private static async Task AttachPlaintextAsync(SqliteConnection connection, string outputPath, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        var escapedPath = outputPath.Replace('\\', '/').Replace("'", "''", StringComparison.Ordinal);
        command.CommandText = $"ATTACH DATABASE '{escapedPath}' AS plaintext KEY '';";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void ValidatePlaintextHeader(string path)
    {
        Span<byte> header = stackalloc byte[16];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length < 100 || stream.Read(header) != header.Length || !header.SequenceEqual("SQLite format 3\0"u8))
        {
            throw new InvalidDataException("The exported database does not have a plaintext SQLite header.");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup. The destination is never deleted here.
        }
    }

    private static async Task<string?> ScalarAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? null : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<byte[]> ReadKeyAsync(Stream input, CancellationToken cancellationToken)
    {
        var header = new byte[ProtocolMagic.Length + 1];
        await ReadExactlyAsync(input, header, cancellationToken).ConfigureAwait(false);
        if (!header.AsSpan(0, ProtocolMagic.Length).SequenceEqual(ProtocolMagic) || header[^1] != KeySize)
        {
            throw new InvalidDataException("The SQLCipher worker key envelope is invalid.");
        }

        var key = new byte[KeySize];
        try
        {
            await ReadExactlyAsync(input, key, cancellationToken).ConfigureAwait(false);
            var trailing = new byte[1];
            try
            {
                if (await input.ReadAsync(trailing, cancellationToken).ConfigureAwait(false) != 0)
                {
                    throw new InvalidDataException("The SQLCipher worker key envelope contains trailing bytes.");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(trailing);
            }

            return key;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(key);
            throw;
        }
    }

    private static async Task ReadExactlyAsync(Stream input, byte[] buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await input.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                throw new EndOfStreamException();
            }

            read += count;
        }
    }

    private static (string InputPath, string OutputPath, string EncryptionProfileId) ParseArguments(string[] args)
    {
        if (args.Length != 6
            || !string.Equals(args[0], "--input", StringComparison.Ordinal)
            || !string.Equals(args[2], "--output", StringComparison.Ordinal)
            || !string.Equals(args[4], "--encryption-profile", StringComparison.Ordinal))
        {
            throw new ArgumentException("The SQLCipher worker accepts only --input, --output, and --encryption-profile.");
        }

        var input = Path.GetFullPath(args[1]);
        var output = Path.GetFullPath(args[3]);
        if (args[1].Length > MaximumPathLength || args[3].Length > MaximumPathLength || !File.Exists(input))
        {
            throw new InvalidDataException("The SQLCipher worker input path is invalid.");
        }

        if (args[5] is not (SqlCipher3Page4096ProfileId or SqlCipher4Page4096ProfileId))
        {
            throw new InvalidDataException("The SQLCipher encryption Profile is not supported.");
        }

        return (input, output, args[5]);
    }

    private sealed class SqlCipherWorkerSqliteException(string stage, int sqliteErrorCode, Exception innerException)
        : InvalidOperationException("The SQLCipher worker SQLite operation failed.", innerException)
    {
        internal string Stage { get; } = stage;
        internal int SqliteErrorCode { get; } = sqliteErrorCode;
    }
}
