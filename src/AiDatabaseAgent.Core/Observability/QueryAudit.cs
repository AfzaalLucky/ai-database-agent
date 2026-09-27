using AiDatabaseAgent.Core.Agent;

namespace AiDatabaseAgent.Core.Observability;

/// <summary>
/// One question as it is stored in the audit log: what was asked, which version and model answered, the SQL
/// that ran, and what it cost. <see cref="TraceId"/> links the row to its distributed trace.
/// </summary>
public sealed record QueryAuditRecord(
    DateTimeOffset AskedAt,
    string? TraceId,
    string Version,
    string Model,
    string Question,
    string Outcome,
    string? Sql,
    string? Message,
    int Attempts,
    int ToolCalls,
    int? RowsReturned,
    bool? Truncated,
    double? EstimatedCost,
    int TotalMs,
    int ModelMs,
    int? QueryMs,
    long? InputTokens,
    long? OutputTokens,
    string? KnowledgeIds)
{
    public static QueryAuditRecord From(AskResult result, DateTimeOffset askedAt, string? traceId)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new QueryAuditRecord(
            askedAt,
            traceId,
            result.Version.ToString(),
            result.Model,
            result.Question,
            result.Outcome.ToString(),
            result.Sql,
            result.Message,
            result.Attempts.Count,
            result.ToolCalls?.Count ?? 0,
            result.Result?.Rows.Count,
            result.Result?.Truncated,
            result.Estimate?.EstimatedCost,
            (int)result.Elapsed.TotalMilliseconds,
            (int)result.Attempts.Sum(a => a.ModelElapsed.TotalMilliseconds),
            result.Result is { } r ? (int)r.Elapsed.TotalMilliseconds : null,
            Sum(result.Attempts.Select(a => a.InputTokens)),
            Sum(result.Attempts.Select(a => a.OutputTokens)),
            result.Knowledge is { } k ? string.Join(",", k.Definitions.Select(d => d.Entry.Id)) : null);
    }

    /// <summary>A question that failed on infrastructure (model, knowledge base or database unreachable).</summary>
    public static QueryAuditRecord ForError(string question, AgentVersion version, string model, string message, TimeSpan elapsed, DateTimeOffset askedAt, string? traceId) =>
        new(askedAt, traceId, version.ToString(), model, question, "Error", null, message, 0, 0, null, null, null,
            (int)elapsed.TotalMilliseconds, 0, null, null, null, null);

    private static long? Sum(IEnumerable<long?> values)
    {
        long total = 0;
        var any = false;
        foreach (var v in values)
        {
            if (v is { } x)
            {
                total += x;
                any = true;
            }
        }
        return any ? total : null;
    }
}

/// <summary>Where answered questions are recorded. Writing must never slow down or fail the answer itself.</summary>
public interface IQueryAuditLog
{
    /// <summary>Queues a record; returns false when it was dropped (log disabled or queue full).</summary>
    bool TryWrite(QueryAuditRecord record);
}
