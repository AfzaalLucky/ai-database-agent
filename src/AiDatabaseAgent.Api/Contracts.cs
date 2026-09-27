using AiDatabaseAgent.Core.Agent;
using AiDatabaseAgent.Core.Knowledge;
using AiDatabaseAgent.Core.Schema;

namespace AiDatabaseAgent.Api;

/// <param name="Version">1, 2 or 3; omitted means the configured default.</param>
public sealed record AskRequest(string Question, AgentVersion? Version);

public sealed record AskResponse(
    string Question,
    AgentVersion Version,
    string Model,
    AskOutcome Outcome,
    string? Sql,
    string? Explanation,
    string? Message,
    IReadOnlyList<ColumnDto> Columns,
    IReadOnlyList<object?[]> Rows,
    bool Truncated,
    EstimateDto? Estimate,
    IReadOnlyList<AttemptDto> Attempts,
    TimingsDto Timings,
    IReadOnlyList<KnowledgeDto> Knowledge,
    IReadOnlyList<ToolCallDto> ToolCalls)
{
    public static AskResponse From(AskResult r)
    {
        ArgumentNullException.ThrowIfNull(r);
        return new AskResponse(
            r.Question,
            r.Version,
            r.Model,
            r.Outcome,
            r.Sql,
            r.Explanation,
            r.Message,
            r.Result?.Columns.Select(c => new ColumnDto(c.Name, c.SqlType)).ToList() ?? [],
            r.Result?.Rows ?? [],
            r.Result?.Truncated ?? false,
            r.Estimate is null ? null : new EstimateDto(Math.Round(r.Estimate.EstimatedCost, 4), Math.Round(r.Estimate.EstimatedRows, 1)),
            r.Attempts.Select(a => new AttemptDto(a.Number, a.Sql, a.Issues, a.ModelResponse, (long)a.ModelElapsed.TotalMilliseconds, a.InputTokens, a.OutputTokens)).ToList(),
            new TimingsDto((long)r.Elapsed.TotalMilliseconds, r.Result is null ? null : (long)r.Result.Elapsed.TotalMilliseconds),
            r.Knowledge?.All.Select(h => new KnowledgeDto(h.Entry.Id, h.Entry.Kind, h.Entry.Title, Math.Round(h.Score, 3), h.MatchedTerm)).ToList() ?? [],
            r.ToolCalls?.Select(t => new ToolCallDto(t.Number, t.Name, t.Arguments, t.Result, t.Succeeded, (long)t.Elapsed.TotalMilliseconds)).ToList() ?? []);
    }
}

/// <param name="MatchedTerm">Set when the entry was found by a literal glossary term; otherwise Score is the vector similarity.</param>
public sealed record ToolCallDto(int Number, string Name, string Arguments, string Result, bool Succeeded, long Ms);

public sealed record KnowledgeDto(string Id, KnowledgeKind Kind, string Title, double Score, string? MatchedTerm);

public sealed record ColumnDto(string Name, string Type);

public sealed record EstimateDto(double Cost, double Rows);

public sealed record AttemptDto(int Number, string? Sql, IReadOnlyList<string> Issues, string ModelResponse, long ModelMs, long? InputTokens, long? OutputTokens);

public sealed record TimingsDto(long TotalMs, long? QueryMs);

public sealed record PromptResponse(AgentVersion Version, IReadOnlyList<PromptMessage> Messages);

public sealed record PromptMessage(string Role, string Text);

public sealed record SchemaResponse(string Database, IReadOnlyList<SchemaTable> Tables);

public sealed class UiOptions
{
    public const string SectionName = "Ui";

    public IList<string> SampleQuestions { get; } = [];
}
