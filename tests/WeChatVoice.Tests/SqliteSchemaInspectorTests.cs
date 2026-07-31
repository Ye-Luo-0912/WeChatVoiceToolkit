using Microsoft.Data.Sqlite;
using WeChatVoice.Core.Models;
using WeChatVoice.Infrastructure.Sqlite;

namespace WeChatVoice.Tests;

public sealed class SqliteSchemaInspectorTests
{
    [Fact]
    public async Task InspectAsync_reports_tables_views_columns_constraints_and_raw_ddl()
    {
        using var temporary = new TestTemporaryDirectory();
        var databasePath = temporary.GetPath("sample.db");
        await CreateSampleDatabaseAsync(databasePath);

        var snapshot = await new SqliteSchemaInspector().InspectAsync(databasePath, CancellationToken.None);

        Assert.Equal(Path.GetFullPath(databasePath), snapshot.DatabasePath);
        Assert.Equal(TimeSpan.Zero, snapshot.InspectedAtUtc.Offset);

        var table = Assert.Single(snapshot.Objects, item => item.Name == "voice_records");
        Assert.Equal(SchemaObjectKind.Table, table.Kind);
        Assert.Contains("CREATE TABLE voice_records", table.DefinitionSql, StringComparison.OrdinalIgnoreCase);

        var identifier = Assert.Single(table.Columns, column => column.Name == "id");
        Assert.True(identifier.IsPrimaryKey);

        var payload = Assert.Single(table.Columns, column => column.Name == "payload");
        Assert.Equal("BLOB", payload.DeclaredType);
        Assert.False(payload.IsNullable);

        var direction = Assert.Single(table.Columns, column => column.Name == "direction");
        Assert.Equal("'incoming'", direction.DefaultValue);

        var view = Assert.Single(snapshot.Objects, item => item.Name == "incoming_voice");
        Assert.Equal(SchemaObjectKind.View, view.Kind);
        Assert.Contains("CREATE VIEW incoming_voice", view.DefinitionSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(view.Columns, column => column.Name == "payload");

        Assert.False(string.IsNullOrWhiteSpace(snapshot.DatabaseSha256));
        Assert.False(string.IsNullOrWhiteSpace(snapshot.SchemaFingerprint));
        Assert.Equal("winsqlite3", snapshot.SQLiteProvider);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.SQLiteVersion));
        Assert.True(snapshot.Pragmas.PageSize > 0);
        Assert.Contains(snapshot.Indexes, index => index.Name == "idx_voice_direction");
        Assert.Contains(snapshot.Triggers, trigger => trigger.Name == "voice_records_audit");

        var shareable = await new SqliteSchemaInspector().InspectAsync(
            databasePath,
            new SchemaInspectionOptions(IncludeLocalPaths: false),
            CancellationToken.None);
        Assert.False(Path.IsPathRooted(shareable.DatabasePath));
        Assert.Null(shareable.LocalPath);
    }

    internal static async Task CreateSampleDatabaseAsync(string databasePath)
    {
        // SqliteSchemaInspector owns provider initialization. Calling it on an
        // empty temporary file initializes the Windows provider before this test
        // creates a harmless local schema through Microsoft.Data.Sqlite.Core.
        File.WriteAllBytes(databasePath, Array.Empty<byte>());
        try
        {
            await new SqliteSchemaInspector().InspectAsync(databasePath, CancellationToken.None);
        }
        catch (SqliteException)
        {
            // An empty file is not always accepted as a read-only database;
            // provider initialization has already completed before open.
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE voice_records (
                id INTEGER PRIMARY KEY,
                payload BLOB NOT NULL,
                direction TEXT NOT NULL DEFAULT 'incoming'
            );
            CREATE VIEW incoming_voice AS
            SELECT id, payload FROM voice_records WHERE direction = 'incoming';
            CREATE INDEX idx_voice_direction ON voice_records(direction);
            CREATE TRIGGER voice_records_audit AFTER INSERT ON voice_records
            BEGIN
                SELECT NEW.id;
            END;
            """;
        await command.ExecuteNonQueryAsync();
    }
}
