using System.Text.RegularExpressions;

namespace AiDatabaseAgent.Core.Prompting;

/// <summary>What the model replied, split into SQL, the optional one-line explanation, or a refusal.</summary>
public sealed record ParsedResponse(string? Sql, string? Explanation, string? CannotAnswerReason);

/// <summary>
/// Extracts SQL from a model reply. Models are asked for one ```sql fenced block, but small local
/// models sometimes omit the fence or the language tag, or wrap the query in prose — all handled.
/// </summary>
public static partial class ModelResponseParser
{
    public const string CannotAnswerMarker = "CANNOT_ANSWER";

    [GeneratedRegex(@"```[ \t]*(?:sql|tsql|t-sql|mssql)?[ \t]*\r?\n(?<code>.*?)```", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FencedBlock();

    [GeneratedRegex(@"CANNOT_ANSWER\s*[:\-]?\s*(?<reason>[^\r\n]*)", RegexOptions.CultureInvariant)]
    private static partial Regex CannotAnswer();

    [GeneratedRegex(@"^\s*(WITH|SELECT)\b", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex QueryStart();

    public static ParsedResponse Parse(string? response)
    {
        var text = (response ?? "").Trim();

        var refusal = CannotAnswer().Match(text);
        if (refusal.Success && !FencedBlock().IsMatch(text))
        {
            var reason = refusal.Groups["reason"].Value.Trim().TrimEnd('.');
            return new ParsedResponse(null, null, reason.Length > 0 ? reason : "The question can't be answered from the available data");
        }

        var fence = FencedBlock().Match(text);
        if (fence.Success)
        {
            var sql = Clean(fence.Groups["code"].Value);
            var prose = Explain(text.Remove(fence.Index, fence.Length));
            return new ParsedResponse(sql.Length > 0 ? sql : null, prose, null);
        }

        // No fence: take everything from the first line that starts a query.
        var start = QueryStart().Match(text);
        if (start.Success)
        {
            var sql = Clean(text[start.Index..]);
            return new ParsedResponse(sql, Explain(text[..start.Index]), null);
        }

        return new ParsedResponse(null, null, null);
    }

    private static string Clean(string sql) => sql.Trim().TrimEnd(';').Trim();

    private static string? Explain(string prose)
    {
        var line = prose
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(l => !l.StartsWith("```", StringComparison.Ordinal));
        return string.IsNullOrWhiteSpace(line) ? null : line.TrimEnd(':');
    }
}
