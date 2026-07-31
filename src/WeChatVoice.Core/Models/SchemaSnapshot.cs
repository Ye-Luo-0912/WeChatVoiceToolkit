using System.Collections.ObjectModel;

namespace WeChatVoice.Core.Models;

/// <summary>
/// A shareable, deterministic description of one SQLite database. Local paths
/// are optional; the default CLI probe emits only a relative display name.
/// </summary>
public sealed record SchemaSnapshot
{
    public SchemaSnapshot(
        string DatabasePath,
        DateTimeOffset InspectedAtUtc,
        IReadOnlyList<SchemaObjectSnapshot>? Objects = null,
        SchemaPragmaSnapshot? Pragmas = null,
        IReadOnlyList<SchemaIndexSnapshot>? Indexes = null,
        IReadOnlyList<SchemaForeignKeySnapshot>? ForeignKeys = null,
        IReadOnlyList<SchemaTriggerSnapshot>? Triggers = null,
        string? DatabaseSha256 = null,
        string? SchemaFingerprint = null,
        string? SQLiteProvider = null,
        string? SQLiteVersion = null,
        SchemaFileCompleteness? FileCompleteness = null,
        string? LocalPath = null)
    {
        if (string.IsNullOrWhiteSpace(DatabasePath))
        {
            throw new ArgumentException("A database path is required.", nameof(DatabasePath));
        }

        this.DatabasePath = DatabasePath;
        this.LocalPath = LocalPath;
        this.InspectedAtUtc = InspectedAtUtc.ToUniversalTime();
        this.Objects = Freeze(Objects);
        this.Pragmas = Pragmas ?? new SchemaPragmaSnapshot();
        this.Indexes = Freeze(Indexes);
        this.ForeignKeys = Freeze(ForeignKeys);
        this.Triggers = Freeze(Triggers);
        this.DatabaseSha256 = DatabaseSha256;
        this.SchemaFingerprint = SchemaFingerprint;
        this.SQLiteProvider = SQLiteProvider;
        this.SQLiteVersion = SQLiteVersion;
        this.FileCompleteness = FileCompleteness ?? new SchemaFileCompleteness();
    }

    public string DatabasePath { get; }

    public string? LocalPath { get; }

    public DateTimeOffset InspectedAtUtc { get; }

    public IReadOnlyList<SchemaObjectSnapshot> Objects { get; }

    public SchemaPragmaSnapshot Pragmas { get; }

    public IReadOnlyList<SchemaIndexSnapshot> Indexes { get; }

    public IReadOnlyList<SchemaForeignKeySnapshot> ForeignKeys { get; }

    public IReadOnlyList<SchemaTriggerSnapshot> Triggers { get; }

    public string? DatabaseSha256 { get; }

    public string? SchemaFingerprint { get; }

    public string? SQLiteProvider { get; }

    public string? SQLiteVersion { get; }

    public SchemaFileCompleteness FileCompleteness { get; }

    private static IReadOnlyList<T> Freeze<T>(IEnumerable<T>? values)
        => new ReadOnlyCollection<T>((values ?? Array.Empty<T>()).ToArray());
}

public enum SchemaObjectKind
{
    Table,
    View,
    VirtualTable,
}

public sealed record SchemaObjectSnapshot
{
    public SchemaObjectSnapshot(
        string Name,
        SchemaObjectKind Kind,
        string? DefinitionSql,
        IReadOnlyList<SchemaColumnSnapshot>? Columns = null)
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new ArgumentException("A schema object name is required.", nameof(Name));
        }

        this.Name = Name;
        this.Kind = Kind;
        this.DefinitionSql = DefinitionSql;
        this.Columns = new ReadOnlyCollection<SchemaColumnSnapshot>((Columns ?? Array.Empty<SchemaColumnSnapshot>()).ToArray());
    }

    public string Name { get; }

    public SchemaObjectKind Kind { get; }

    public string? DefinitionSql { get; }

    public IReadOnlyList<SchemaColumnSnapshot> Columns { get; }
}

public sealed record SchemaColumnSnapshot
{
    public SchemaColumnSnapshot(
        int Ordinal,
        string Name,
        string? DeclaredType,
        bool IsPrimaryKey,
        bool IsNullable,
        string? DefaultValue,
        bool IsHidden = false,
        bool IsGenerated = false,
        string? GeneratedExpression = null)
    {
        this.Ordinal = Ordinal;
        this.Name = Name;
        this.DeclaredType = DeclaredType;
        this.IsPrimaryKey = IsPrimaryKey;
        this.IsNullable = IsNullable;
        this.DefaultValue = DefaultValue;
        this.IsHidden = IsHidden;
        this.IsGenerated = IsGenerated;
        this.GeneratedExpression = GeneratedExpression;
    }

    public int Ordinal { get; }
    public string Name { get; }
    public string? DeclaredType { get; }
    public bool IsPrimaryKey { get; }
    public bool IsNullable { get; }
    public string? DefaultValue { get; }
    public bool IsHidden { get; }
    public bool IsGenerated { get; }
    public string? GeneratedExpression { get; }
}

public sealed record SchemaPragmaSnapshot(
    long UserVersion = 0,
    long ApplicationId = 0,
    long PageSize = 0,
    string? Encoding = null,
    long SchemaVersion = 0,
    bool ForeignKeysEnabled = false);

public sealed record SchemaIndexSnapshot(
    string TableName,
    string Name,
    bool IsUnique,
    bool IsPartial,
    IReadOnlyList<SchemaIndexColumnSnapshot> Columns);

public sealed record SchemaIndexColumnSnapshot(int Sequence, int ColumnOrdinal, string? ColumnName, string? Collation, bool Descending);

public sealed record SchemaForeignKeySnapshot(
    string TableName,
    int Id,
    int Sequence,
    string ReferencedTable,
    string? FromColumn,
    string? ToColumn,
    string OnUpdate,
    string OnDelete,
    string Match);

public sealed record SchemaTriggerSnapshot(string Name, string TableName, string? DefinitionSql);

public sealed record SchemaFileCompleteness(
    bool WalPresent = false,
    bool ShmPresent = false,
    bool IsWalPairComplete = true,
    string? CompletenessIssue = null);
