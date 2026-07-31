using System.Collections.ObjectModel;

namespace WeChatVoice.Core.Models;

/// <summary>
/// A read-only structural description of a database. It intentionally contains
/// no application-specific assumptions about WeChat table or column names.
/// </summary>
public sealed record SchemaSnapshot
{
    public SchemaSnapshot(
        string DatabasePath,
        DateTimeOffset InspectedAtUtc,
        IEnumerable<SchemaObjectSnapshot>? Objects = null)
    {
        if (string.IsNullOrWhiteSpace(DatabasePath))
        {
            throw new ArgumentException("A database path is required.", nameof(DatabasePath));
        }

        this.DatabasePath = Path.GetFullPath(DatabasePath);
        this.InspectedAtUtc = InspectedAtUtc.ToUniversalTime();
        this.Objects = new ReadOnlyCollection<SchemaObjectSnapshot>((Objects ?? Array.Empty<SchemaObjectSnapshot>()).ToArray());
    }

    public string DatabasePath { get; }

    public DateTimeOffset InspectedAtUtc { get; }

    public IReadOnlyList<SchemaObjectSnapshot> Objects { get; }
}

public enum SchemaObjectKind
{
    Table,
    View,
}

/// <summary>
/// A table or view along with its raw DDL and exposed columns.
/// </summary>
public sealed record SchemaObjectSnapshot
{
    public SchemaObjectSnapshot(
        string Name,
        SchemaObjectKind Kind,
        string? DefinitionSql,
        IEnumerable<SchemaColumnSnapshot>? Columns = null)
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

/// <summary>
/// Structural metadata for one column in a table or view.
/// </summary>
public sealed record SchemaColumnSnapshot(
    int Ordinal,
    string Name,
    string? DeclaredType,
    bool IsPrimaryKey,
    bool IsNullable,
    string? DefaultValue);
