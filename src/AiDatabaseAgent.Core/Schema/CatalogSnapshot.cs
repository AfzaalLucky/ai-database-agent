namespace AiDatabaseAgent.Core.Schema;

/// <summary>
/// Raw catalog metadata as read from the database, before visibility filtering. Kept separate from
/// <see cref="DatabaseSchema"/> so the assembly rules (what the model may see, how keys are carried
/// through views) are pure code that can be unit-tested without a server.
/// </summary>
public sealed record CatalogSnapshot(
    string DatabaseName,
    IReadOnlyList<CatalogObject> Objects,
    IReadOnlyList<CatalogColumn> Columns,
    IReadOnlyList<CatalogForeignKey> ForeignKeys,
    IReadOnlyList<CatalogCheckConstraint> CheckConstraints,
    IReadOnlySet<ObjectColumn> Selectable);

public sealed record CatalogObject(string Schema, string Name, TableKind Kind, string? Description, IReadOnlyList<string> PrimaryKey);

/// <param name="SourceSchema">For view columns: the base table column it comes from (null when computed or unknown).</param>
public sealed record CatalogColumn(
    string Schema,
    string Table,
    string Name,
    int Ordinal,
    string SqlType,
    bool IsNullable,
    bool IsIdentity,
    bool IsComputed,
    string? Description,
    string? SourceSchema = null,
    string? SourceTable = null,
    string? SourceColumn = null);

/// <summary>One foreign key; <see cref="Columns"/> and <see cref="ReferencedColumns"/> are aligned by position.</summary>
public sealed record CatalogForeignKey(
    string Name,
    string Schema,
    string Table,
    IReadOnlyList<string> Columns,
    string ReferencedSchema,
    string ReferencedTable,
    IReadOnlyList<string> ReferencedColumns);

/// <summary>A column-level CHECK constraint with its normalised definition from sys.check_constraints.</summary>
public sealed record CatalogCheckConstraint(string Schema, string Table, string Column, string Definition);

/// <summary>A (schema, table-or-view, column) triple; comparisons are case-insensitive.</summary>
public readonly record struct ObjectColumn(string Schema, string Table, string Column)
{
    public bool Equals(ObjectColumn other) =>
        string.Equals(Schema, other.Schema, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Table, other.Table, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Column, other.Column, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() => HashCode.Combine(
        StringComparer.OrdinalIgnoreCase.GetHashCode(Schema),
        StringComparer.OrdinalIgnoreCase.GetHashCode(Table),
        StringComparer.OrdinalIgnoreCase.GetHashCode(Column));
}
