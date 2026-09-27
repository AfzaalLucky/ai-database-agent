using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using AiDatabaseAgent.Core.Execution;
using AiDatabaseAgent.Core.Knowledge;
using AiDatabaseAgent.Core.Prompting;
using AiDatabaseAgent.Core.Schema;
using AiDatabaseAgent.Core.Validation;
using Microsoft.Extensions.AI;

namespace AiDatabaseAgent.Core.Tools;

/// <summary>One tool invocation, as recorded for the response trace and the UI.</summary>
public sealed record ToolCall(int Number, string Name, string Arguments, string Result, bool Succeeded, TimeSpan Elapsed);

/// <summary>
/// The tools the V5 agent can call. One instance per question: it records every call. Tools never throw to
/// the model — failures come back as text the model can act on ("No table named …", "Rejected: …").
/// Everything that touches data goes through the same gates as V3: names are checked against the agent's
/// schema, SQL through <see cref="SqlValidator"/> and the cost limit, and execution as the agent user.
/// </summary>
public sealed class AgentToolbox(
    DatabaseSchema schema,
    IQueryExecutor executor,
    IKnowledgeRetriever? knowledge,
    double maxEstimatedCost,
    int timeoutSeconds)
{
    public const int PreviewRows = 20;
    private const int MaxResultChars = 4000;

    private readonly List<ToolCall> _calls = [];

    public IReadOnlyList<ToolCall> Calls => _calls;

    /// <summary>True once search_glossary has returned a NOT TRACKED entry — a documented reason to decline.</summary>
    public bool GlossaryReportedNotTracked { get; set; }

    /// <summary>
    /// A refusal is suspicious when the model's last attempt to run a query failed and the glossary never said
    /// the data is missing: it has probably given up on a detail (a guessed column name), not on missing data.
    /// </summary>
    public bool RefusalLooksPremature =>
        !GlossaryReportedNotTracked && _calls.LastOrDefault(c => c.Name == "run_query") is { Succeeded: false } && _lastQueryFailureFixable;

    // Whether the most recent failed run_query failed on something the model can fix (names, syntax, a SQL
    // Server error) — as opposed to a security rejection or the cost limit, after which declining is right.
    private bool _lastQueryFailureFixable;

    /// <summary>The tools, as function declarations for the chat client.</summary>
    public IList<AITool> AsTools() =>
    [
        AIFunctionFactory.Create(ListTables, "list_tables"),
        AIFunctionFactory.Create(DescribeTable, "describe_table"),
        AIFunctionFactory.Create(SearchGlossary, "search_glossary"),
        AIFunctionFactory.Create(SampleValues, "sample_values"),
        AIFunctionFactory.Create(RunQuery, "run_query"),
    ];

    [Description("Lists every table and view you can query, each with a one-line description.")]
    public string ListTables() => Record("list_tables", "{}", () =>
    {
        var sb = new StringBuilder();
        foreach (var t in schema.Tables)
            sb.Append(t.QualifiedName).Append(t.Kind == TableKind.View ? " (view)" : "").Append(" — ").Append(FirstSentence(t.Description)).Append('\n');
        return (sb.ToString(), true);
    });

    [Description("Shows a table's columns with types, primary key, foreign keys (join paths, written as ->), descriptions and allowed values.")]
    public string DescribeTable([Description("Schema-qualified table or view name, e.g. sales.Orders")] string table) =>
        Record("describe_table", $$"""{"table":"{{table}}"}""", () =>
        {
            var t = Resolve(table);
            return t is null
                ? ($"No table named '{table}'. Call list_tables to see the available tables.", false)
                : (SchemaAwarePromptBuilder.RenderSchema(new DatabaseSchema(schema.DatabaseName, [t])), true);
        });

    [Description("Searches the business glossary: definitions of metrics and terms (revenue, average order value, last month, active customer, ...), data that is NOT tracked, and verified example queries. Use it for any business term in the question.")]
    public async Task<string> SearchGlossary([Description("The business term or question to look up")] string query, CancellationToken cancellationToken = default) =>
        await RecordAsync("search_glossary", $$"""{"query":"{{query}}"}""", async () =>
        {
            if (knowledge is null)
                return ("The glossary is not available.", false);
            var context = await knowledge.RetrieveAsync(query, cancellationToken);
            if (!context.All.Any())
                return ("No glossary entries match. Use the schema descriptions.", true);
            var sb = new StringBuilder();
            if (context.Definitions.Any(h => h.Entry.Kind == KnowledgeKind.NotTracked))
                GlossaryReportedNotTracked = true;
            foreach (var h in context.Definitions)
            {
                sb.Append(h.Entry.Kind == KnowledgeKind.NotTracked ? "NOT TRACKED — " : "").Append(h.Entry.Title).Append(": ").Append(h.Entry.Text).Append('\n');
                if (h.Entry.Sql is not null)
                    sb.Append("  Pattern: ").Append(h.Entry.Sql).Append('\n');
            }
            foreach (var h in context.Examples)
                sb.Append("Example — ").Append(h.Entry.Question).Append('\n').Append(h.Entry.Sql).Append('\n');
            return (sb.ToString(), true);
        });

    [Description("Returns up to 'max' distinct values of a column — use it to check how text values are spelled before filtering on them.")]
    public async Task<string> SampleValues(
        [Description("Schema-qualified table or view name, e.g. inventory.Warehouses")] string table,
        [Description("Column name")] string column,
        [Description("How many distinct values to return (1-50)")] int max = 20,
        CancellationToken cancellationToken = default) =>
        await RecordAsync("sample_values", $$"""{"table":"{{table}}","column":"{{column}}","max":{{max}}}""", async () =>
        {
            var t = Resolve(table);
            if (t is null)
                return ($"No table named '{table}'. Call list_tables to see the available tables.", false);
            var c = t.Column(column);
            if (c is null)
                return ($"No column '{column}' on {t.QualifiedName}. Its columns are: {string.Join(", ", t.Columns.Select(x => x.Name))}.", false);

            // Names come from the discovered schema, never from the model's text, and are bracket-quoted.
            var n = Math.Clamp(max, 1, 50);
            var sql = $"SELECT DISTINCT TOP ({n}) {Quote(c.Name)} FROM {Quote(t.Schema)}.{Quote(t.Name)} WHERE {Quote(c.Name)} IS NOT NULL ORDER BY {Quote(c.Name)}";
            try
            {
                var r = await executor.ExecuteAsync(sql, new QueryLimits(n, timeoutSeconds), cancellationToken);
                return (string.Join(", ", r.Rows.Select(row => Format(row[0]))), true);
            }
            catch (QueryExecutionException ex)
            {
                return ($"SQL Server error: {ex.Message}", false);
            }
        });

    [Description("Runs a read-only T-SQL SELECT to test it and returns the first 20 rows, or exactly why it was rejected or failed. Use it to check your query before giving the final answer.")]
    public async Task<string> RunQuery([Description("One T-SQL SELECT statement")] string sql, CancellationToken cancellationToken = default) =>
        await RecordAsync("run_query", $$"""{"sql":{{System.Text.Json.JsonSerializer.Serialize(sql)}}}""", async () =>
        {
            var parsed = ModelResponseParser.Parse(sql);
            var query = parsed.Sql ?? sql.Trim().TrimEnd(';');
            var validation = SqlValidator.Validate(query, schema);
            if (!validation.IsValid)
            {
                _lastQueryFailureFixable = !validation.IsSecurityViolation;
                return ("Rejected:\n- " + string.Join("\n- ", validation.Issues.Select(i => i.Message)), false);
            }
            try
            {
                var estimate = await executor.EstimateAsync(query, cancellationToken);
                if (estimate.EstimatedCost > maxEstimatedCost)
                {
                    _lastQueryFailureFixable = false;
                    return (string.Create(CultureInfo.InvariantCulture, $"Rejected: estimated cost {estimate.EstimatedCost:0.##} exceeds the limit {maxEstimatedCost:0.##}. Narrow the query."), false);
                }
                var r = await executor.ExecuteAsync(query, new QueryLimits(PreviewRows, timeoutSeconds), cancellationToken);
                return (Table(r), true);
            }
            catch (QueryExecutionException ex)
            {
                _lastQueryFailureFixable = ex.ErrorNumber != -2; // a timeout is not a detail to fix
                return ($"SQL Server error: {ex.Message}", false);
            }
        });

    private SchemaTable? Resolve(string name)
    {
        var parts = (name ?? "").Replace("[", "", StringComparison.Ordinal).Replace("]", "", StringComparison.Ordinal).Split('.', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            >= 2 => schema.Find(parts[^2], parts[^1]),
            1 => schema.Tables.Count(t => string.Equals(t.Name, parts[0], StringComparison.OrdinalIgnoreCase)) == 1
                ? schema.Tables.First(t => string.Equals(t.Name, parts[0], StringComparison.OrdinalIgnoreCase))
                : null,
            _ => null,
        };
    }

    private static string Quote(string identifier) => "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";

    private static string FirstSentence(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";
        var i = text.IndexOf(". ", StringComparison.Ordinal);
        return i > 0 ? text[..(i + 1)] : text;
    }

    private static string Format(object? v) => v switch
    {
        null => "NULL",
        DateTime d => d.TimeOfDay == TimeSpan.Zero ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : d.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

    private static string Table(QueryResult r)
    {
        var sb = new StringBuilder();
        sb.AppendJoin(" | ", r.Columns.Select(c => c.Name)).Append('\n');
        foreach (var row in r.Rows)
            sb.AppendJoin(" | ", row.Select(Format)).Append('\n');
        sb.Append(r.Truncated ? $"(first {r.Rows.Count} rows; more exist)" : $"({r.Rows.Count} row{(r.Rows.Count == 1 ? "" : "s")})");
        return sb.ToString();
    }

    private string Record(string name, string args, Func<(string Result, bool Ok)> body)
    {
        var clock = Stopwatch.StartNew();
        var (result, ok) = body();
        return Add(name, args, result, ok, clock.Elapsed);
    }

    private async Task<string> RecordAsync(string name, string args, Func<Task<(string Result, bool Ok)>> body)
    {
        var clock = Stopwatch.StartNew();
        var (result, ok) = await body();
        return Add(name, args, result, ok, clock.Elapsed);
    }

    private string Add(string name, string args, string result, bool ok, TimeSpan elapsed)
    {
        var text = result.Length > MaxResultChars ? result[..MaxResultChars] + "\n(truncated)" : result;
        lock (_calls)
            _calls.Add(new ToolCall(_calls.Count + 1, name, args, text, ok, elapsed));
        return text;
    }
}
