using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using WeChatVoice.Core.Models;
using WeChatVoice.Core.Ports;
using WeChatVoice.Infrastructure.Serialization;

namespace WeChatVoice.Infrastructure.Sqlite;

/// <summary>
/// Inspects SQLite structure through a read-only connection. The inspector
/// records enough metadata for adapter selection without interpreting WeChat
/// table names or guessing product semantics.
/// </summary>
public sealed class SqliteSchemaInspector : ISchemaInspector
{
    public Task<SchemaSnapshot> InspectAsync(string databasePath, CancellationToken cancellationToken)
        => InspectAsync(databasePath, new SchemaInspectionOptions(IncludeLocalPaths: true), cancellationToken);

    public async Task<SchemaSnapshot> InspectAsync(
        string databasePath,
        SchemaInspectionOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        var fullDatabasePath = Path.GetFullPath(databasePath);
        if (!File.Exists(fullDatabasePath))
        {
            throw new FileNotFoundException("The SQLite database file was not found.", fullDatabasePath);
        }

        WindowsSqliteProvider.EnsureInitialized();
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullDatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DllNotFoundException exception)
        {
            throw new PlatformNotSupportedException(
                "The Windows winsqlite3 library required for SQLite schema inspection is unavailable on this system.",
                exception);
        }

        await ExecuteNonQueryAsync(connection, "PRAGMA query_only = ON;", cancellationToken).ConfigureAwait(false);
        var pragma = await ReadPragmasAsync(connection, cancellationToken).ConfigureAwait(false);
        var sqliteVersion = await ReadStringAsync(connection, "SELECT sqlite_version();", cancellationToken).ConfigureAwait(false);

        var schemaRows = await ReadSchemaRowsAsync(connection, cancellationToken).ConfigureAwait(false);
        var objects = new List<SchemaObjectSnapshot>(schemaRows.Count);
        var indexes = new List<SchemaIndexSnapshot>();
        var foreignKeys = new List<SchemaForeignKeySnapshot>();
        foreach (var row in schemaRows)
        {
            var columns = await ReadColumnsAsync(connection, row.Name, cancellationToken).ConfigureAwait(false);
            objects.Add(new SchemaObjectSnapshot(row.Name, row.Kind, row.DefinitionSql, columns));
            if (row.Kind == SchemaObjectKind.Table)
            {
                indexes.AddRange(await ReadIndexesAsync(connection, row.Name, cancellationToken).ConfigureAwait(false));
                foreignKeys.AddRange(await ReadForeignKeysAsync(connection, row.Name, cancellationToken).ConfigureAwait(false));
            }
        }

        var triggers = await ReadTriggersAsync(connection, cancellationToken).ConfigureAwait(false);
        var walPath = options.WalPath ?? fullDatabasePath + "-wal";
        var shmPath = options.ShmPath ?? fullDatabasePath + "-shm";
        var walPresent = File.Exists(walPath);
        var shmPresent = File.Exists(shmPath);
        var completeness = new SchemaFileCompleteness(
            walPresent,
            shmPresent,
            walPresent == shmPresent,
            walPresent == shmPresent ? null : "SQLite WAL and SHM sidecars are not present as a complete pair.");

        var hash = options.PrecomputedSha256 ?? await FileHashing.ComputeSha256Async(fullDatabasePath, cancellationToken).ConfigureAwait(false);
        var fingerprint = ComputeFingerprint(objects, indexes, foreignKeys, triggers, pragma);
        var displayPath = options.IncludeLocalPaths ? fullDatabasePath : Path.GetFileName(fullDatabasePath);
        return new SchemaSnapshot(
            displayPath,
            DateTimeOffset.UtcNow,
            objects,
            pragma,
            indexes,
            foreignKeys,
            triggers,
            hash,
            fingerprint,
            "winsqlite3",
            sqliteVersion,
            completeness,
            options.IncludeLocalPaths ? fullDatabasePath : null);
    }

    private static async Task<IReadOnlyList<SchemaObjectRow>> ReadSchemaRowsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name, type, sql
            FROM sqlite_master
            WHERE type IN ('table', 'view')
              AND name NOT LIKE 'sqlite_%'
            ORDER BY CASE type WHEN 'table' THEN 0 ELSE 1 END, name COLLATE NOCASE;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<SchemaObjectRow>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var name = reader.GetString(0);
            var type = reader.GetString(1);
            var sql = reader.IsDBNull(2) ? null : reader.GetString(2);
            var kind = string.Equals(type, "view", StringComparison.OrdinalIgnoreCase)
                ? SchemaObjectKind.View
                : sql?.Contains("CREATE VIRTUAL TABLE", StringComparison.OrdinalIgnoreCase) == true
                    ? SchemaObjectKind.VirtualTable
                    : SchemaObjectKind.Table;
            rows.Add(new SchemaObjectRow(name, kind, sql));
        }

        return rows;
    }

    private static async Task<IReadOnlyList<SchemaColumnSnapshot>> ReadColumnsAsync(
        SqliteConnection connection,
        string objectName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_xinfo('{Escape(objectName)}');";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var columns = new List<SchemaColumnSnapshot>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var hidden = reader.FieldCount > 6 && !reader.IsDBNull(6) ? reader.GetInt64(6) : 0;
            var generated = hidden is 2 or 3;
            columns.Add(new SchemaColumnSnapshot(
                checked(Convert.ToInt32(reader.GetInt64(0))),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetInt64(5) > 0,
                reader.GetInt64(3) == 0,
                reader.IsDBNull(4) ? null : reader.GetString(4),
                hidden != 0,
                generated));
        }

        return columns;
    }

    private static async Task<IReadOnlyList<SchemaIndexSnapshot>> ReadIndexesAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA index_list('{Escape(tableName)}');";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var indexes = new List<SchemaIndexSnapshot>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var name = reader.GetString(1);
            var columns = await ReadIndexColumnsAsync(connection, name, cancellationToken).ConfigureAwait(false);
            indexes.Add(new SchemaIndexSnapshot(tableName, name, reader.GetInt64(2) != 0, reader.FieldCount > 4 && reader.GetInt64(4) != 0, columns));
        }

        return indexes;
    }

    private static async Task<IReadOnlyList<SchemaIndexColumnSnapshot>> ReadIndexColumnsAsync(
        SqliteConnection connection,
        string indexName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA index_xinfo('{Escape(indexName)}');";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var columns = new List<SchemaIndexColumnSnapshot>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var cid = reader.GetInt64(1);
            columns.Add(new SchemaIndexColumnSnapshot(
                checked(Convert.ToInt32(reader.GetInt64(0))),
                checked(Convert.ToInt32(cid)),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.FieldCount > 4 && !reader.IsDBNull(4) ? reader.GetString(4) : null,
                reader.FieldCount > 3 && reader.GetInt64(3) != 0));
        }

        return columns;
    }

    private static async Task<IReadOnlyList<SchemaForeignKeySnapshot>> ReadForeignKeysAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA foreign_key_list('{Escape(tableName)}');";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var keys = new List<SchemaForeignKeySnapshot>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            keys.Add(new SchemaForeignKeySnapshot(
                tableName,
                checked(Convert.ToInt32(reader.GetInt64(0))),
                checked(Convert.ToInt32(reader.GetInt64(1))),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7)));
        }

        return keys;
    }

    private static async Task<IReadOnlyList<SchemaTriggerSnapshot>> ReadTriggersAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name, tbl_name, sql FROM sqlite_master WHERE type = 'trigger' ORDER BY name COLLATE NOCASE;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var triggers = new List<SchemaTriggerSnapshot>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            triggers.Add(new SchemaTriggerSnapshot(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        return triggers;
    }

    private static async Task<SchemaPragmaSnapshot> ReadPragmasAsync(SqliteConnection connection, CancellationToken cancellationToken)
        => new(
            await ReadInt64Async(connection, "PRAGMA user_version;", cancellationToken).ConfigureAwait(false),
            await ReadInt64Async(connection, "PRAGMA application_id;", cancellationToken).ConfigureAwait(false),
            await ReadInt64Async(connection, "PRAGMA page_size;", cancellationToken).ConfigureAwait(false),
            await ReadStringAsync(connection, "PRAGMA encoding;", cancellationToken).ConfigureAwait(false),
            await ReadInt64Async(connection, "PRAGMA schema_version;", cancellationToken).ConfigureAwait(false),
            await ReadInt64Async(connection, "PRAGMA foreign_keys;", cancellationToken).ConfigureAwait(false) != 0);

    private static async Task<long> ReadInt64Async(SqliteConnection connection, string sql, CancellationToken cancellationToken)
        => Convert.ToInt64(await ExecuteScalarAsync(connection, sql, cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<string?> ReadStringAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
        => Convert.ToString(await ExecuteScalarAsync(connection, sql, cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<object?> ExecuteScalarAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteNonQueryAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string ComputeFingerprint(
        IReadOnlyList<SchemaObjectSnapshot> objects,
        IReadOnlyList<SchemaIndexSnapshot> indexes,
        IReadOnlyList<SchemaForeignKeySnapshot> foreignKeys,
        IReadOnlyList<SchemaTriggerSnapshot> triggers,
        SchemaPragmaSnapshot pragmas)
    {
        var canonical = new
        {
            Objects = objects.Select(static item => new { item.Name, item.Kind, item.DefinitionSql, item.Columns }).ToArray(),
            Indexes = indexes,
            ForeignKeys = foreignKeys,
            Triggers = triggers,
            Pragmas = new
            {
                pragmas.UserVersion,
                pragmas.ApplicationId,
                pragmas.PageSize,
                pragmas.Encoding,
                pragmas.SchemaVersion,
            },
        };
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(canonical, new JsonSerializerOptions { WriteIndented = false }));
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static string Escape(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private sealed record SchemaObjectRow(string Name, SchemaObjectKind Kind, string? DefinitionSql);
}
