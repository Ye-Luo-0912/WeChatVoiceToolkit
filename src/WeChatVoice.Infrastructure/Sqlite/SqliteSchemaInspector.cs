using Microsoft.Data.Sqlite;
using WeChatVoice.Core.Models;
using WeChatVoice.Core.Ports;

namespace WeChatVoice.Infrastructure.Sqlite;

/// <summary>
/// Inspects SQLite metadata through a read-only connection. It deliberately
/// reports structure only; it never interprets product-specific tables or data.
/// </summary>
public sealed class SqliteSchemaInspector : ISchemaInspector
{
    public async Task<SchemaSnapshot> InspectAsync(string databasePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
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
            Pooling = false
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

        // The connection string already prevents writes. This defensive pragma
        // additionally makes accidental write statements fail on this connection.
        await using (var queryOnly = connection.CreateCommand())
        {
            queryOnly.CommandText = "PRAGMA query_only = ON;";
            await queryOnly.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var schemaRows = new List<SchemaObjectRow>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT name, type, sql
                FROM sqlite_master
                WHERE type IN ('table', 'view')
                  AND name NOT LIKE 'sqlite_%'
                ORDER BY CASE type WHEN 'table' THEN 0 ELSE 1 END, name COLLATE NOCASE;
                """;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var name = reader.GetString(0);
                var type = reader.GetString(1);
                var kind = string.Equals(type, "table", StringComparison.OrdinalIgnoreCase)
                    ? SchemaObjectKind.Table
                    : SchemaObjectKind.View;
                var definitionSql = reader.IsDBNull(2) ? null : reader.GetString(2);
                schemaRows.Add(new SchemaObjectRow(name, kind, definitionSql));
            }
        }

        var objects = new List<SchemaObjectSnapshot>(schemaRows.Count);
        foreach (var row in schemaRows)
        {
            var columns = await ReadColumnsAsync(connection, row.Name, cancellationToken).ConfigureAwait(false);
            objects.Add(new SchemaObjectSnapshot(row.Name, row.Kind, row.DefinitionSql, columns));
        }

        return new SchemaSnapshot(fullDatabasePath, DateTimeOffset.UtcNow, objects);
    }

    private static async Task<IReadOnlyList<SchemaColumnSnapshot>> ReadColumnsAsync(
        SqliteConnection connection,
        string objectName,
        CancellationToken cancellationToken)
    {
        var escapedName = objectName.Replace("'", "''", StringComparison.Ordinal);
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info('{escapedName}');";

        var columns = new List<SchemaColumnSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var ordinal = checked(Convert.ToInt32(reader.GetInt64(0)));
            var name = reader.GetString(1);
            var declaredType = reader.IsDBNull(2) ? null : reader.GetString(2);
            var isNullable = reader.GetInt64(3) == 0;
            var defaultValue = reader.IsDBNull(4) ? null : reader.GetString(4);
            var isPrimaryKey = reader.GetInt64(5) > 0;

            columns.Add(new SchemaColumnSnapshot(
                ordinal,
                name,
                declaredType,
                isPrimaryKey,
                isNullable,
                defaultValue));
        }

        return columns;
    }

    private sealed record SchemaObjectRow(string Name, SchemaObjectKind Kind, string? DefinitionSql);
}
