using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

if (args.Length != 2 || !string.Equals(args[0], "--output", StringComparison.Ordinal))
{
    return 2;
}

var output = Path.GetFullPath(args[1]);
var key = Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray();
try
{
    if (File.Exists(output))
    {
        return 2;
    }

    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlcipher());
    SQLitePCL.raw.FreezeProvider();
    var keyHex = Convert.ToHexString(key);
    var connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = output,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Private,
        Pooling = false,
    }.ToString();
    await using var connection = new SqliteConnection(connectionString);
    await connection.OpenAsync();
    await ExecuteAsync(connection, $"PRAGMA key = \"x'{keyHex}'\";");
    await ExecuteAsync(connection, "PRAGMA cipher_compatibility = 4;");
    await ExecuteAsync(connection, "CREATE TABLE fixture(value TEXT NOT NULL); INSERT INTO fixture(value) VALUES ('ok');");
    return 0;
}
catch
{
    return 1;
}
finally
{
    CryptographicOperations.ZeroMemory(key);
}

static async Task ExecuteAsync(SqliteConnection connection, string sql)
{
    await using var command = connection.CreateCommand();
    command.CommandText = sql;
    await command.ExecuteNonQueryAsync();
}
