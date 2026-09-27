using AiDatabaseAgent.Core.Schema;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace AiDatabaseAgent.Core.Validation;

public enum IssueKind
{
    /// <summary>The text is not valid T-SQL.</summary>
    Syntax,

    /// <summary>Not exactly one read-only SELECT (more statements, DML/DDL, SELECT INTO, ...).</summary>
    NotReadOnly,

    /// <summary>A construct outside the allow-list: other databases/servers, OPENROWSET, variables, UDFs, hints, ...</summary>
    Forbidden,

    /// <summary>A table or view that is not in the agent's schema.</summary>
    UnknownTable,

    /// <summary>A column that does not exist on the table it is taken from.</summary>
    UnknownColumn,
}

/// <param name="FoundOn">For <see cref="IssueKind.UnknownColumn"/>: where the column does exist, if anywhere — the data is there, only the query is wrong.</param>
public sealed record ValidationIssue(IssueKind Kind, string Message, string? FoundOn = null);

public sealed record ValidationResult(IReadOnlyList<ValidationIssue> Issues)
{
    public bool IsValid => Issues.Count == 0;

    /// <summary>Security findings (as opposed to "the model got a name wrong").</summary>
    public bool IsSecurityViolation => Issues.Any(i => i.Kind is IssueKind.NotReadOnly or IssueKind.Forbidden);
}

/// <summary>
/// Application-level gate for model-generated SQL (V3), using Microsoft's own T-SQL parser (ScriptDom).
/// Allow-list based: exactly one SELECT (CTEs allowed) reading only tables and views from the agent's
/// schema, through a small set of known-safe table-source kinds. Everything else is refused before the
/// query reaches the database. The database's own least-privilege identity remains the second line.
/// </summary>
public static class SqlValidator
{
    private static readonly HashSet<string> AllowedTableHints = new(StringComparer.OrdinalIgnoreCase) { "NoLock", "ReadUncommitted" };

    private static readonly HashSet<string> AllowedTableFunctions = new(StringComparer.OrdinalIgnoreCase) { "STRING_SPLIT", "GENERATE_SERIES" };

    private static readonly HashSet<string> AllowedQueryHints = new(StringComparer.OrdinalIgnoreCase) { "Recompile", "MaxDop" };

    private static readonly HashSet<string> DatePartFunctions = new(StringComparer.OrdinalIgnoreCase) { "DATEPART", "DATEADD", "DATEDIFF", "DATEDIFF_BIG", "DATENAME", "DATETRUNC", "DATE_BUCKET" };

    public static ValidationResult Validate(string sql, DatabaseSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var issues = new List<ValidationIssue>();

        var parser = new TSql170Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(sql ?? "");
        var fragment = parser.Parse(reader, out var errors);
        if (errors.Count > 0)
        {
            issues.AddRange(errors.Take(3).Select(e => new ValidationIssue(IssueKind.Syntax, $"Syntax error at line {e.Line}, column {e.Column}: {e.Message}")));
            return new ValidationResult(issues);
        }

        var statements = ((TSqlScript)fragment).Batches.SelectMany(b => b.Statements).ToList();
        if (statements.Count != 1)
        {
            issues.Add(new ValidationIssue(IssueKind.NotReadOnly, $"Exactly one statement is allowed; found {statements.Count}."));
            return new ValidationResult(issues);
        }
        if (statements[0] is not SelectStatement select)
        {
            issues.Add(new ValidationIssue(IssueKind.NotReadOnly, $"Only SELECT queries are allowed; found {Describe(statements[0])}."));
            return new ValidationResult(issues);
        }
        if (select.Into is not null)
            issues.Add(new ValidationIssue(IssueKind.NotReadOnly, "SELECT ... INTO creates a table and is not allowed."));

        var scope = new ScopeCollector(schema);
        select.Accept(scope);
        issues.AddRange(scope.Issues);
        if (issues.Count == 0)
            issues.AddRange(CheckColumns(scope, schema));

        return new ValidationResult(issues.Distinct().ToList());
    }

    private static string Describe(TSqlStatement s) => s.GetType().Name.Replace("Statement", "", StringComparison.Ordinal) switch
    {
        "" => "a statement",
        var name => $"a {name.ToUpperInvariant()} statement",
    };

    private static IEnumerable<ValidationIssue> CheckColumns(ScopeCollector scope, DatabaseSchema schema)
    {
        foreach (var col in scope.ColumnReferences)
        {
            var parts = col.MultiPartIdentifier.Identifiers.Select(i => i.Value).ToList();
            var name = parts[^1];

            if (parts.Count == 1)
            {
                if (scope.ReferencedTables.Any(t => t.Column(name) is not null)
                    || scope.OutputNames.Contains(name)
                    || scope.HasOpaqueSource)
                    continue;
                var tables = scope.ReferencedTables.Select(t => t.QualifiedName).Distinct().ToList();
                yield return new ValidationIssue(IssueKind.UnknownColumn, tables.Count == 0
                    ? $"Column '{name}' does not exist."
                    : $"Column '{name}' does not exist on {string.Join(" or ", tables)}.");
                continue;
            }

            var qualifier = parts[^2];
            if (parts.Count >= 3)
            {
                var table = schema.Find(parts[^3], qualifier);
                if (table is not null && table.Column(name) is null)
                    yield return UnknownColumn(name, table);
                continue;
            }

            if (!scope.Aliases.TryGetValue(qualifier, out var sources))
            {
                // A bare table name used as a qualifier, e.g. Orders.OrderId with FROM sales.Orders.
                var byName = scope.ReferencedTables.Where(t => string.Equals(t.Name, qualifier, StringComparison.OrdinalIgnoreCase)).ToList();
                if (byName.Count == 0)
                {
                    yield return new ValidationIssue(IssueKind.UnknownTable, $"'{qualifier}' in '{qualifier}.{name}' is not a table or alias in this query.");
                    continue;
                }
                sources = byName.Select(t => new AliasSource(t, null)).ToList();
            }

            if (sources.Any(s => s.Table is null && (s.Columns is null || s.Columns.Contains(name))))
                continue; // derived table / CTE exposing the column (or with unknown columns)
            var baseTables = sources.Where(s => s.Table is not null).Select(s => s.Table!).ToList();
            if (baseTables.Count > 0 && baseTables.All(t => t.Column(name) is null))
                yield return UnknownColumn(name, baseTables[0], scope, schema);
            else if (baseTables.Count == 0)
                yield return new ValidationIssue(IssueKind.UnknownColumn, $"Column '{name}' is not produced by '{qualifier}'.");
        }
    }

    private static ValidationIssue UnknownColumn(string name, SchemaTable table) =>
        new(IssueKind.UnknownColumn, $"Column '{name}' does not exist on {table.QualifiedName}. Its columns are: {string.Join(", ", table.Columns.Select(c => c.Name))}.");

    private static ValidationIssue UnknownColumn(string name, SchemaTable table, ScopeCollector scope, DatabaseSchema schema)
    {
        var (hint, foundOn) = WhereElse(name, table, scope, schema);
        var issue = UnknownColumn(name, table);
        return issue with { Message = issue.Message + hint, FoundOn = foundOn };
    }

    /// <summary>
    /// When a column is taken from the wrong table, say where it really is — first among the tables already
    /// in the query (with their aliases), then anywhere in the schema. Without this hint a model told only
    /// "no such column" tends to conclude the data doesn't exist and give up.
    /// </summary>
    private static (string? Hint, string? FoundOn) WhereElse(string column, SchemaTable wrong, ScopeCollector scope, DatabaseSchema schema)
    {
        var inQuery = scope.Aliases
            .SelectMany(a => a.Value.Where(s => s.Table is not null && s.Table != wrong && s.Table.Column(column) is not null)
                .Select(s => string.Equals(a.Key, s.Table!.Name, StringComparison.OrdinalIgnoreCase) ? s.Table.QualifiedName : $"{s.Table.QualifiedName} (alias {a.Key})"))
            .Distinct()
            .ToList();
        if (inQuery.Count > 0)
            return ($" It exists on {string.Join(" and ", inQuery)}, which is already in the query.", string.Join(", ", inQuery));

        var elsewhere = schema.Tables.Where(t => t != wrong && t.Column(column) is not null).Select(t => t.QualifiedName).ToList();
        return elsewhere.Count > 0 ? ($" It exists on {string.Join(", ", elsewhere)}.", string.Join(", ", elsewhere)) : (null, null);
    }

    /// <summary>What an alias in the query refers to: a schema table, or a derived table/CTE with (possibly unknown) output columns.</summary>
    private sealed record AliasSource(SchemaTable? Table, HashSet<string>? Columns);

    /// <summary>Walks the statement once: enforces the allow-list and collects tables, aliases and column references.</summary>
    private sealed class ScopeCollector(DatabaseSchema schema) : TSqlFragmentVisitor
    {
        private readonly HashSet<string> _cteNames = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HashSet<string>?> _cteColumns = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<TSqlFragment> _datePartArguments = [];

        public List<ValidationIssue> Issues { get; } = [];

        public List<SchemaTable> ReferencedTables { get; } = [];

        public Dictionary<string, List<AliasSource>> Aliases { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Select-list aliases and derived/CTE column names: valid as bare names (ORDER BY, outer queries).</summary>
        public HashSet<string> OutputNames { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<ColumnReferenceExpression> ColumnReferences { get; } = [];

        /// <summary>True when some source's columns can't be known statically (e.g. SELECT * in a CTE, OPENJSON without a schema).</summary>
        public bool HasOpaqueSource { get; private set; }

        public override void ExplicitVisit(SelectStatement node)
        {
            // Register CTE names first so references to them aren't treated as unknown tables.
            if (node.WithCtesAndXmlNamespaces is { } with)
            {
                if (with.XmlNamespaces is not null)
                    Forbid("WITH XMLNAMESPACES");
                foreach (var cte in with.CommonTableExpressions)
                {
                    _cteNames.Add(cte.ExpressionName.Value);
                    var cols = cte.Columns.Count > 0
                        ? cte.Columns.Select(c => c.Value).ToHashSet(StringComparer.OrdinalIgnoreCase)
                        : OutputColumns(cte.QueryExpression);
                    _cteColumns[cte.ExpressionName.Value] = cols;
                    if (cols is not null)
                        OutputNames.UnionWith(cols);
                }
            }
            if (node.On is not null)
                Forbid("SELECT ... ON filegroup");
            base.ExplicitVisit(node);
        }

        public override void Visit(TableReference node)
        {
            switch (node)
            {
                case NamedTableReference named:
                    Named(named);
                    break;
                case QueryDerivedTable derived:
                    var cols = derived.Columns.Count > 0
                        ? derived.Columns.Select(c => c.Value).ToHashSet(StringComparer.OrdinalIgnoreCase)
                        : OutputColumns(derived.QueryExpression);
                    if (cols is not null)
                        OutputNames.UnionWith(cols);
                    else
                        HasOpaqueSource = true;
                    if (derived.Alias is not null)
                        AddAlias(derived.Alias.Value, new AliasSource(null, cols));
                    break;
                case InlineDerivedTable inline:
                    var inlineCols = inline.Columns.Select(c => c.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    OutputNames.UnionWith(inlineCols);
                    if (inline.Alias is not null)
                        AddAlias(inline.Alias.Value, new AliasSource(null, inlineCols));
                    break;
                case GlobalFunctionTableReference fn:
                    // STRING_SPLIT, GENERATE_SERIES: built-in, read nothing from the database.
                    if (!AllowedTableFunctions.Contains(fn.Name.Value))
                        Forbid($"table function {fn.Name.Value}");
                    HasOpaqueSource = true;
                    if (fn.Alias is not null)
                        AddAlias(fn.Alias.Value, new AliasSource(null, null));
                    break;
                case OpenJsonTableReference json:
                    HasOpaqueSource = true;
                    if (json.Alias is not null)
                        AddAlias(json.Alias.Value, new AliasSource(null, null));
                    break;
                case JoinTableReference or JoinParenthesisTableReference:
                    break; // containers; their children are visited separately
                default:
                    Forbid(node switch
                    {
                        OpenRowsetTableReference or InternalOpenRowset or BulkOpenRowset => "OPENROWSET",
                        OpenQueryTableReference => "OPENQUERY (linked servers)",
                        AdHocTableReference => "OPENDATASOURCE",
                        OpenXmlTableReference => "OPENXML",
                        VariableTableReference => "table variables",
                        SchemaObjectFunctionTableReference f => $"table-valued function {f.SchemaObject.BaseIdentifier.Value}",
                        _ => $"table source '{node.GetType().Name}'",
                    });
                    break;
            }
            base.Visit(node);
        }

        private void Named(NamedTableReference named)
        {
            var name = named.SchemaObject;
            if (name.ServerIdentifier is not null)
            {
                Forbid($"linked server '{name.ServerIdentifier.Value}'");
                return;
            }
            if (name.DatabaseIdentifier is not null && !string.Equals(name.DatabaseIdentifier.Value, schema.DatabaseName, StringComparison.OrdinalIgnoreCase))
            {
                Forbid($"database '{name.DatabaseIdentifier.Value}' (only {schema.DatabaseName} may be queried)");
                return;
            }
            foreach (var hint in named.TableHints)
                if (!AllowedTableHints.Contains(hint.HintKind.ToString()))
                    Forbid($"table hint {hint.HintKind}");
            if (named.TableSampleClause is not null)
                Forbid("TABLESAMPLE");
            if (named.ForPath)
                Forbid("FOR PATH");
            if (named.TemporalClause is not null)
                Forbid("FOR SYSTEM_TIME");

            var table = name.SchemaIdentifier?.Value;
            var objectName = name.BaseIdentifier.Value;
            var alias = named.Alias?.Value ?? objectName;

            if (table is null && _cteNames.Contains(objectName))
            {
                var cols = _cteColumns.GetValueOrDefault(objectName);
                if (cols is null)
                    HasOpaqueSource = true;
                AddAlias(alias, new AliasSource(null, cols));
                return;
            }
            if (objectName.StartsWith('#'))
            {
                Forbid("temporary tables");
                return;
            }

            SchemaTable? resolved;
            if (table is not null)
            {
                resolved = schema.Find(table, objectName);
            }
            else
            {
                var matches = schema.Tables.Where(t => string.Equals(t.Name, objectName, StringComparison.OrdinalIgnoreCase)).ToList();
                resolved = matches.Count == 1 ? matches[0] : null;
                if (matches.Count > 1)
                {
                    Issues.Add(new ValidationIssue(IssueKind.UnknownTable, $"Table '{objectName}' is ambiguous; qualify it with its schema ({string.Join(", ", matches.Select(m => m.QualifiedName))})."));
                    return;
                }
            }

            if (resolved is null)
            {
                var qualified = table is null ? objectName : $"{table}.{objectName}";
                Issues.Add(new ValidationIssue(IssueKind.UnknownTable,
                    $"Table '{qualified}' does not exist or is not available. Available tables: {string.Join(", ", schema.Tables.Select(t => t.QualifiedName))}."));
                return;
            }
            ReferencedTables.Add(resolved);
            AddAlias(alias, new AliasSource(resolved, null));
        }

        public override void Visit(FunctionCall node)
        {
            if (node.CallTarget is not null)
                Forbid($"user-defined or method function '{node.FunctionName.Value}'");
            if (DatePartFunctions.Contains(node.FunctionName.Value) && node.Parameters.Count > 0)
                _datePartArguments.Add(node.Parameters[0]); // DATEPART(month, ...) parses "month" as a column
            base.Visit(node);
        }

        public override void Visit(SelectScalarExpression node)
        {
            if (node.ColumnName?.Value is { } alias)
                OutputNames.Add(alias);
            base.Visit(node);
        }

        public override void Visit(ColumnReferenceExpression node)
        {
            if (node.ColumnType == ColumnType.Regular && node.MultiPartIdentifier is not null && !_datePartArguments.Contains(node))
                ColumnReferences.Add(node);
            base.Visit(node);
        }

        public override void Visit(VariableReference node)
        {
            Forbid($"variable {node.Name}");
            base.Visit(node);
        }

        public override void Visit(GlobalVariableExpression node)
        {
            Forbid($"system variable {node.Name}");
            base.Visit(node);
        }

        public override void Visit(OptimizerHint node)
        {
            if (!AllowedQueryHints.Contains(node.HintKind.ToString()))
                Forbid($"query hint {node.HintKind}");
            base.Visit(node);
        }

        private void AddAlias(string alias, AliasSource source)
        {
            if (!Aliases.TryGetValue(alias, out var list))
                Aliases[alias] = list = [];
            list.Add(source);
        }

        private void Forbid(string what) =>
            Issues.Add(new ValidationIssue(IssueKind.Forbidden, $"Not allowed: {what}."));

        /// <summary>Output column names of a query, or null when they can't be known (e.g. SELECT *).</summary>
        private static HashSet<string>? OutputColumns(QueryExpression query)
        {
            while (query is BinaryQueryExpression binary)
                query = binary.FirstQueryExpression;
            while (query is QueryParenthesisExpression paren)
                query = paren.QueryExpression;
            if (query is not QuerySpecification spec)
                return null;

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var element in spec.SelectElements)
            {
                switch (element)
                {
                    case SelectStarExpression:
                        return null;
                    case SelectScalarExpression { ColumnName: { } alias }:
                        names.Add(alias.Value);
                        break;
                    case SelectScalarExpression { Expression: ColumnReferenceExpression { MultiPartIdentifier: { } id } }:
                        names.Add(id.Identifiers[^1].Value);
                        break;
                }
            }
            return names;
        }
    }
}
