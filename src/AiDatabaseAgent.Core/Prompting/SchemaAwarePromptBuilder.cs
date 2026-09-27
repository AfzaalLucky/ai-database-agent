using System.Globalization;
using System.Text;
using AiDatabaseAgent.Core.Agent;
using AiDatabaseAgent.Core.Knowledge;
using AiDatabaseAgent.Core.Schema;
using Microsoft.Extensions.AI;

namespace AiDatabaseAgent.Core.Prompting;

/// <summary>
/// V2 prompt: gives the model what a person writing the query would need —
/// <list type="bullet">
///   <item>the schema as compact DDL (the format code models have seen most), with types, nullability,
///         primary keys, foreign-key join paths, descriptions and allowed enum values;</item>
///   <item>T-SQL dialect rules (TOP not LIMIT, date functions, schema-qualified names);</item>
///   <item>domain notes the schema can't express, and today's date;</item>
///   <item>a few worked examples;</item>
///   <item>a way out: <c>CANNOT_ANSWER</c> when the data isn't there, instead of inventing columns.</item>
/// </list>
/// </summary>
public static class SchemaAwarePromptBuilder
{
    public const string SystemPrompt = """
        You are an expert Microsoft SQL Server developer. You write one read-only T-SQL query that answers
        the user's question using ONLY the tables and columns in the schema provided.

        Rules:
        - T-SQL only: use TOP (n), never LIMIT; use GETDATE(), DATEADD, DATEDIFF, DATEFROMPARTS, EOMONTH, YEAR(), MONTH().
        - Always write schema-qualified table names (e.g. sales.Orders) and give tables short aliases.
        - Join only along the foreign keys shown (the "->" arrows). Never guess a column that is not listed.
        - For enum-like columns, compare only with the listed values, spelled exactly.
        - Read the descriptions: they define business meaning (for example how revenue is calculated).
        - One SELECT statement (common table expressions allowed). Never modify data.
        - For "top N" or "most/least" questions use TOP (n) with an ORDER BY. Order results when order matters.
        - Name computed columns with clear aliases. Round money to 2 decimals.
        - Before answering, check that every table and column you need is listed in the schema above. The schema
          is complete: anything not listed does not exist and must not be used or guessed.
        - If the question needs data that is not in the schema, or asks to change data, do not write a query:
          reply with exactly: CANNOT_ANSWER: <one short reason>

        Reply format: one short sentence saying what the query returns, then the query in a ```sql block.
        """;

    public static List<ChatMessage> Build(
        DatabaseSchema schema,
        string question,
        IEnumerable<string> domainNotes,
        IEnumerable<FewShotExample> examples,
        DateTimeOffset now,
        KnowledgeContext? knowledge = null)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(domainNotes);
        ArgumentNullException.ThrowIfNull(examples);

        var context = new StringBuilder();
        context.Append("Database: ").Append(schema.DatabaseName).Append(" (Microsoft SQL Server)\n\n");
        context.Append(RenderSchema(schema));

        var notes = domainNotes.Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        context.Append("\nNotes:\n- Today's date is ").Append(now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(".\n");
        foreach (var note in notes)
            context.Append("- ").Append(note.Trim()).Append('\n');

        // V4: business definitions retrieved for this question. They override general assumptions.
        if (knowledge is { Definitions.Count: > 0 })
        {
            context.Append("\nBusiness definitions that apply to this question (follow them exactly; entries marked NOT TRACKED")
                   .Append(" describe data that does not exist — if the question needs it, reply CANNOT_ANSWER):\n");
            foreach (var hit in knowledge.Definitions)
            {
                var e = hit.Entry;
                context.Append("- ").Append(e.Kind == KnowledgeKind.NotTracked ? "NOT TRACKED — " : "").Append(e.Title).Append(": ").Append(OneLine(e.Text)).Append('\n');
                if (!string.IsNullOrWhiteSpace(e.Sql))
                    context.Append("  Pattern: ").Append(OneLine(e.Sql)).Append('\n');
            }
        }

        var messages = new List<ChatMessage> { new(ChatRole.System, SystemPrompt + "\n\n" + context) };

        // Retrieved examples (V4) come after the fixed ones, closest to the question.
        var retrieved = knowledge?.Examples
            .Where(h => h.Entry.Question is not null && h.Entry.Sql is not null)
            .Select(h => new FewShotExample(h.Entry.Question!, h.Entry.Sql!, h.Entry.Text)) ?? [];

        // Worked examples as prior turns: the model imitates the format as well as the style.
        foreach (var ex in examples.Concat(retrieved))
        {
            messages.Add(new ChatMessage(ChatRole.User, ex.Question));
            // The sentence is imitated too, so it must describe the result — a placeholder like
            // "Here is the query." gets copied verbatim into every answer.
            var sentence = string.IsNullOrWhiteSpace(ex.Explanation) ? $"Answers: {ex.Question.TrimEnd('?')}." : ex.Explanation.Trim();
            messages.Add(new ChatMessage(ChatRole.Assistant, $"{sentence}\n```sql\n{ex.Sql.Trim()}\n```"));
        }

        messages.Add(new ChatMessage(ChatRole.User, question));
        return messages;
    }

    /// <summary>
    /// Renders the schema as annotated DDL, e.g.
    /// <code>
    /// -- Sales orders (header). ...
    /// TABLE sales.Orders (
    ///   OrderId int PRIMARY KEY,
    ///   CustomerId int NOT NULL -> sales.Customers.CustomerId,
    ///   Status varchar(10) NOT NULL, -- Pending, Shipped, ... | values: 'Cancelled', 'Delivered', ...
    /// )
    /// </code>
    /// </summary>
    public static string RenderSchema(DatabaseSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var sb = new StringBuilder();
        foreach (var t in schema.Tables)
        {
            if (!string.IsNullOrWhiteSpace(t.Description))
                sb.Append("-- ").Append(OneLine(t.Description)).Append('\n');
            sb.Append(t.Kind == TableKind.View ? "VIEW " : "TABLE ").Append(t.QualifiedName).Append(" (\n");

            for (var i = 0; i < t.Columns.Count; i++)
            {
                var c = t.Columns[i];
                sb.Append("  ").Append(c.Name).Append(' ').Append(c.SqlType);
                if (t.PrimaryKey.Count == 1 && string.Equals(t.PrimaryKey[0], c.Name, StringComparison.OrdinalIgnoreCase))
                    sb.Append(" PRIMARY KEY");
                else
                    sb.Append(c.IsNullable ? " NULL" : " NOT NULL");
                if (c.IsComputed)
                    sb.Append(" COMPUTED");

                var fk = t.ForeignKeys.FirstOrDefault(k => k.Columns.Count == 1 && string.Equals(k.Columns[0], c.Name, StringComparison.OrdinalIgnoreCase));
                if (fk is not null)
                    sb.Append(" -> ").Append(fk.ReferencedQualifiedName).Append('.').Append(fk.ReferencedColumns[0]);

                if (i < t.Columns.Count - 1)
                    sb.Append(',');

                var comment = new List<string>();
                if (!string.IsNullOrWhiteSpace(c.Description))
                    comment.Add(OneLine(c.Description));
                if (c.AllowedValues.Count > 0)
                    comment.Add("values: " + string.Join(", ", c.AllowedValues.Select(v => $"'{v}'")));
                if (comment.Count > 0)
                    sb.Append(" -- ").AppendJoin(" | ", comment);
                sb.Append('\n');
            }

            if (t.PrimaryKey.Count > 1)
                sb.Append("  PRIMARY KEY (").AppendJoin(", ", t.PrimaryKey).Append(")\n");
            foreach (var fk in t.ForeignKeys.Where(k => k.Columns.Count > 1))
                sb.Append("  (").AppendJoin(", ", fk.Columns).Append(") -> ").Append(fk.ReferencedQualifiedName).Append(" (").AppendJoin(", ", fk.ReferencedColumns).Append(")\n");
            sb.Append(")\n\n");
        }
        return sb.ToString();
    }

    private static string OneLine(string s) => string.Join(' ', s.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
