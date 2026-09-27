using System.Globalization;
using System.Text;
using AiDatabaseAgent.Core.Schema;
using Microsoft.Extensions.AI;

namespace AiDatabaseAgent.Core.Prompting;

/// <summary>
/// V5 prompts. <see cref="WithTools"/> (default) adds tool guidance to V4's full context. <see cref="Build"/> is
/// the "minimal" pure-agent variant: only table names, the rules and a way of working — the model looks up
/// everything through tools, so the prompt stays the same size however large the database grows. Measured on
/// the development set it scored far below V4 with the same model, so it is kept as an option, not the default.
/// </summary>
public static class AgentPromptBuilder
{
    public const string SystemPrompt = """
        You are a careful data analyst answering questions about a Microsoft SQL Server database by writing ONE
        read-only T-SQL query. You have tools to explore the database before answering.

        How to work:
        1. For any business term in the question (revenue, average order value, last month, active customer, margin, ...)
           call search_glossary and follow the definition exactly. If the glossary says the data is NOT TRACKED, stop
           and reply CANNOT_ANSWER.
        2. Call describe_table for every table you intend to use. Use only columns it shows, and join only along the
           "->" foreign keys.
        3. Before filtering on a text value, call sample_values to see how it is spelled.
        4. Call run_query with your query and check the rows look right; fix it if run_query reports a problem.
        5. Finish with one short sentence describing the result, then the final query in a ```sql block.

        Rules: T-SQL only (TOP (n), never LIMIT); schema-qualified table names; one SELECT (CTEs allowed); never
        modify data. If the question needs data that does not exist in the database, or asks to change data, reply
        with exactly: CANNOT_ANSWER: <one short reason>
        """;

    /// <summary>Appended to V4's schema-aware system prompt in V5's default (full-context) mode.</summary>
    public const string ToolGuidance = """

        You also have tools. Before giving your final answer:
        - Test your query with run_query and check the rows make sense (row count, magnitudes, no duplicates from joins).
          If run_query reports a problem, fix the query and test again.
        - Before filtering on a text value, check its spelling with sample_values.
        - For a business term not defined above, call search_glossary; for a table you need more detail on, describe_table.
        Then reply with one short sentence and the final query in a ```sql block — or CANNOT_ANSWER: <reason> if the data
        does not exist.
        """;

    /// <summary>V5's default prompt: V4's full context with tool guidance added to the system message.</summary>
    public static List<ChatMessage> WithTools(List<ChatMessage> v4Messages)
    {
        ArgumentNullException.ThrowIfNull(v4Messages);
        var system = v4Messages[0];
        v4Messages[0] = new ChatMessage(ChatRole.System, system.Text + ToolGuidance);
        return v4Messages;
    }

    public static List<ChatMessage> Build(DatabaseSchema schema, string question, IEnumerable<string> domainNotes, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(domainNotes);

        var sb = new StringBuilder(SystemPrompt);
        sb.Append("\n\nDatabase: ").Append(schema.DatabaseName).Append(" (Microsoft SQL Server). Tables and views:\n");
        foreach (var t in schema.Tables)
            sb.Append("- ").Append(t.QualifiedName).Append('\n');
        sb.Append("\nNotes:\n- Today's date is ").Append(now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(".\n");
        foreach (var note in domainNotes.Where(n => !string.IsNullOrWhiteSpace(n)))
            sb.Append("- ").Append(note.Trim()).Append('\n');

        return [new ChatMessage(ChatRole.System, sb.ToString()), new ChatMessage(ChatRole.User, question)];
    }
}
