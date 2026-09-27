using System.Text.RegularExpressions;

namespace AiDatabaseAgent.Core.Schema;

/// <summary>
/// Turns a raw <see cref="CatalogSnapshot"/> into the <see cref="DatabaseSchema"/> the model is shown:
/// <list type="bullet">
///   <item>only objects and columns the agent identity can SELECT are included;</item>
///   <item>view columns inherit the description and allowed values of the base column they expose;</item>
///   <item>foreign keys are carried onto views, and a key that points at a hidden table is redirected
///         to a visible view exposing the same columns (so joins through hr.EmployeeDirectory still
///         appear even though hr.Employees is hidden);</item>
///   <item>a view gets its base table's primary key when it exposes all of its columns.</item>
/// </list>
/// </summary>
public static class SchemaAssembler
{
    public static DatabaseSchema Assemble(CatalogSnapshot catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var cmp = StringComparer.OrdinalIgnoreCase;

        var allColumns = catalog.Columns.ToDictionary(c => new ObjectColumn(c.Schema, c.Table, c.Name));
        var visibleColumns = catalog.Columns
            .Where(c => catalog.Selectable.Contains(new ObjectColumn(c.Schema, c.Table, c.Name)))
            .ToList();
        var visibleObjects = catalog.Objects
            .Where(o => visibleColumns.Any(c => cmp.Equals(c.Schema, o.Schema) && cmp.Equals(c.Table, o.Name)))
            .ToList();

        // Base column -> every visible (object, column) that exposes it: itself, or view columns sourced from it.
        var exposures = new Dictionary<ObjectColumn, List<ObjectColumn>>();
        foreach (var c in visibleColumns)
        {
            var self = new ObjectColumn(c.Schema, c.Table, c.Name);
            var baseCol = c.SourceTable is null ? self : new ObjectColumn(c.SourceSchema ?? c.Schema, c.SourceTable, c.SourceColumn ?? c.Name);
            if (!exposures.TryGetValue(baseCol, out var list))
                exposures[baseCol] = list = [];
            list.Add(self);
        }

        CatalogColumn BaseOf(CatalogColumn c) =>
            c.SourceTable is not null && allColumns.TryGetValue(new ObjectColumn(c.SourceSchema ?? c.Schema, c.SourceTable, c.SourceColumn ?? c.Name), out var src)
                ? src
                : c;

        var tables = new List<SchemaTable>();
        foreach (var obj in visibleObjects.OrderBy(o => o.Schema, cmp).ThenBy(o => o.Name, cmp))
        {
            var cols = visibleColumns
                .Where(c => cmp.Equals(c.Schema, obj.Schema) && cmp.Equals(c.Table, obj.Name))
                .OrderBy(c => c.Ordinal)
                .ToList();

            var schemaColumns = cols.Select(c =>
            {
                var src = BaseOf(c);
                var allowed = catalog.CheckConstraints
                    .Where(k => cmp.Equals(k.Schema, src.Schema) && cmp.Equals(k.Table, src.Table) && cmp.Equals(k.Column, src.Name))
                    .Select(k => CheckConstraintParser.TryParseAllowedValues(k.Definition, src.Name))
                    .FirstOrDefault(v => v is not null) ?? [];
                return new SchemaColumn(c.Name, c.SqlType, c.IsNullable, c.IsIdentity, c.IsComputed, c.Description ?? src.Description, allowed);
            }).ToList();

            // Map this object's column names back to base columns (identity for tables).
            var byBase = cols.ToDictionary(c => { var b = BaseOf(c); return new ObjectColumn(b.Schema, b.Table, b.Name); }, c => c.Name);

            tables.Add(new SchemaTable(
                obj.Schema,
                obj.Name,
                obj.Kind,
                obj.Description,
                schemaColumns,
                PrimaryKeyFor(obj, cols, catalog, byBase),
                ForeignKeysFor(obj, catalog, byBase, exposures)));
        }

        return new DatabaseSchema(catalog.DatabaseName, tables);
    }

    private static List<string> PrimaryKeyFor(CatalogObject obj, List<CatalogColumn> cols, CatalogSnapshot catalog, Dictionary<ObjectColumn, string> byBase)
    {
        if (obj.Kind == TableKind.Table)
            return obj.PrimaryKey.Where(k => cols.Any(c => string.Equals(c.Name, k, StringComparison.OrdinalIgnoreCase))).ToList();

        // A view inherits the primary key of a base table whose key columns it exposes in full.
        foreach (var baseObj in catalog.Objects.Where(o => o.Kind == TableKind.Table && o.PrimaryKey.Count > 0))
        {
            var mapped = baseObj.PrimaryKey.Select(k => byBase.GetValueOrDefault(new ObjectColumn(baseObj.Schema, baseObj.Name, k))).ToList();
            if (mapped.All(m => m is not null))
                return mapped!;
        }
        return [];
    }

    private static List<ForeignKey> ForeignKeysFor(CatalogObject obj, CatalogSnapshot catalog, Dictionary<ObjectColumn, string> byBase, Dictionary<ObjectColumn, List<ObjectColumn>> exposures)
    {
        var result = new List<ForeignKey>();
        foreach (var fk in catalog.ForeignKeys)
        {
            // Does this object expose every column of the key's source side?
            var local = fk.Columns.Select(c => byBase.GetValueOrDefault(new ObjectColumn(fk.Schema, fk.Table, c))).ToList();
            if (local.Any(l => l is null))
                continue;

            // Find a visible object exposing every referenced column. Prefer the referenced table itself,
            // then this same object (self-references through a view), then any other view.
            var refCols = fk.ReferencedColumns.Select(c => new ObjectColumn(fk.ReferencedSchema, fk.ReferencedTable, c)).ToList();
            var candidates = (exposures.GetValueOrDefault(refCols[0]) ?? [])
                .Select(e => (e.Schema, e.Table))
                .Distinct()
                .Where(owner => refCols.All(rc => (exposures.GetValueOrDefault(rc) ?? []).Any(e => Same((e.Schema, e.Table), owner.Schema, owner.Table))))
                .ToList();
            if (candidates.Count == 0)
                continue;

            var target = candidates.FirstOrDefault(c => Same(c, fk.ReferencedSchema, fk.ReferencedTable));
            if (target == default)
                target = candidates.FirstOrDefault(c => Same(c, obj.Schema, obj.Name));
            if (target == default)
                target = candidates.OrderBy(c => c.Schema, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Table, StringComparer.OrdinalIgnoreCase).First();

            var targetCols = refCols
                .Select(rc => exposures[rc].First(e => Same((e.Schema, e.Table), target.Schema, target.Table)).Column)
                .ToList();

            result.Add(new ForeignKey(fk.Name, local!, target.Schema, target.Table, targetCols));
        }
        return result;
    }

    private static bool Same((string Schema, string Table) a, string schema, string name) =>
        string.Equals(a.Schema, schema, StringComparison.OrdinalIgnoreCase) && string.Equals(a.Table, name, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Reads enum-like CHECK constraints. SQL Server stores <c>Status IN ('A','B')</c> normalised as
/// <c>([Status]='B' OR [Status]='A')</c>; this recovers the value list.
/// </summary>
public static partial class CheckConstraintParser
{
    [GeneratedRegex(@"^\(*\[(?<col>[^\]]+)\]\s*=\s*N?'(?<val>(?:[^']|'')*)'\)*$", RegexOptions.CultureInvariant)]
    private static partial Regex EqualityTerm();

    /// <returns>The allowed values (sorted), or null if the definition is not a pure OR-list of equalities on <paramref name="column"/>.</returns>
    public static IReadOnlyList<string>? TryParseAllowedValues(string definition, string column)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var body = definition.Trim();
        while (body.StartsWith('(') && body.EndsWith(')') && Balanced(body[1..^1]))
            body = body[1..^1].Trim();

        var values = new List<string>();
        foreach (var term in body.Split(" OR ", StringSplitOptions.TrimEntries))
        {
            var m = EqualityTerm().Match(term);
            if (!m.Success || !string.Equals(m.Groups["col"].Value, column, StringComparison.OrdinalIgnoreCase))
                return null;
            values.Add(m.Groups["val"].Value.Replace("''", "'", StringComparison.Ordinal));
        }
        values.Sort(StringComparer.Ordinal);
        return values.Count > 0 ? values : null;
    }

    private static bool Balanced(string s)
    {
        var depth = 0;
        foreach (var ch in s)
        {
            depth += ch == '(' ? 1 : ch == ')' ? -1 : 0;
            if (depth < 0)
                return false;
        }
        return depth == 0;
    }
}
