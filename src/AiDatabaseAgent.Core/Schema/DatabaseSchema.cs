namespace AiDatabaseAgent.Core.Schema;

/// <summary>
/// The part of a database the agent is allowed to query, as discovered from the catalog views
/// under the agent's own (least-privilege) identity. Anything not in here is invisible to the model.
/// </summary>
public sealed record DatabaseSchema(string DatabaseName, IReadOnlyList<SchemaTable> Tables)
{
    /// <summary>Finds a table or view by schema and name (case-insensitive, like the server collation).</summary>
    public SchemaTable? Find(string schema, string name) =>
        Tables.FirstOrDefault(t =>
            string.Equals(t.Schema, schema, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
}

public enum TableKind
{
    Table,
    View,
}

public sealed record SchemaTable(
    string Schema,
    string Name,
    TableKind Kind,
    string? Description,
    IReadOnlyList<SchemaColumn> Columns,
    IReadOnlyList<string> PrimaryKey,
    IReadOnlyList<ForeignKey> ForeignKeys)
{
    public string QualifiedName => $"{Schema}.{Name}";

    public SchemaColumn? Column(string name) =>
        Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <param name="SqlType">The declared type as written in T-SQL, e.g. <c>decimal(10,2)</c> or <c>nvarchar(60)</c>.</param>
/// <param name="AllowedValues">Values permitted by a CHECK constraint of the form <c>col IN (...)</c>, if any.</param>
public sealed record SchemaColumn(
    string Name,
    string SqlType,
    bool IsNullable,
    bool IsIdentity,
    bool IsComputed,
    string? Description,
    IReadOnlyList<string> AllowedValues);

/// <summary>A foreign key from <see cref="Columns"/> of the owning table to <see cref="ReferencedColumns"/>.</summary>
public sealed record ForeignKey(
    string Name,
    IReadOnlyList<string> Columns,
    string ReferencedSchema,
    string ReferencedTable,
    IReadOnlyList<string> ReferencedColumns)
{
    public string ReferencedQualifiedName => $"{ReferencedSchema}.{ReferencedTable}";
}

public interface ISchemaProvider
{
    /// <summary>Returns the queryable schema. Implementations may cache it.</summary>
    Task<DatabaseSchema> GetSchemaAsync(CancellationToken cancellationToken = default);
}
