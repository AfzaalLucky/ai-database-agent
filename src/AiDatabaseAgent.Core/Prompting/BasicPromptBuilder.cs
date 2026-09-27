using System.Text;
using AiDatabaseAgent.Core.Schema;
using Microsoft.Extensions.AI;

namespace AiDatabaseAgent.Core.Prompting;

/// <summary>
/// V1 prompt: the simplest thing that can work — table and column names, the question, "reply with SQL".
/// No types, keys, descriptions or dialect rules, which is exactly what V2 sets out to fix.
/// </summary>
public static class BasicPromptBuilder
{
    public const string SystemPrompt =
        "You translate questions into SQL for a Microsoft SQL Server database. " +
        "Reply with a single T-SQL query in a ```sql code block and nothing else.";

    public static List<ChatMessage> Build(DatabaseSchema schema, string question)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var sb = new StringBuilder("Tables:\n");
        foreach (var t in schema.Tables)
            sb.Append("- ").Append(t.QualifiedName).Append('(').AppendJoin(", ", t.Columns.Select(c => c.Name)).Append(")\n");
        sb.Append("\nQuestion: ").Append(question);

        return [new(ChatRole.System, SystemPrompt), new(ChatRole.User, sb.ToString())];
    }
}
