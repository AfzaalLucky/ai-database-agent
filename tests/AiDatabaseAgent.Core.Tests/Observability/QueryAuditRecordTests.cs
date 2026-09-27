using AiDatabaseAgent.Core.Agent;
using AiDatabaseAgent.Core.Execution;
using AiDatabaseAgent.Core.Knowledge;
using AiDatabaseAgent.Core.Observability;

namespace AiDatabaseAgent.Core.Tests.Observability;

public class QueryAuditRecordTests
{
    private static readonly DateTimeOffset AskedAt = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Sums_model_time_and_tokens_across_attempts()
    {
        var result = new AskResult(
            "Order statuses?", AgentVersion.V4, "qwen2.5-coder:7b", AskOutcome.Answered,
            "SELECT Status FROM sales.Orders", "Lists statuses.",
            new QueryResult([new ResultColumn("Status", "varchar")], [["Shipped"], ["Pending"]], Truncated: false, TimeSpan.FromMilliseconds(4)),
            null,
            [
                new AgentAttempt(1, "reply", "SELECT Salary FROM sales.Orders", ["no Salary"], TimeSpan.FromMilliseconds(1200), 2500, 30),
                new AgentAttempt(2, "reply", "SELECT Status FROM sales.Orders", [], TimeSpan.FromMilliseconds(800), 2700, 25),
            ],
            new QueryCostEstimate(0.0237, 2),
            TimeSpan.FromMilliseconds(2100),
            new KnowledgeContext([new KnowledgeHit(new KnowledgeEntry("order-status", KnowledgeKind.Definition, "Order status", "text"), 1)], []));

        var r = QueryAuditRecord.From(result, AskedAt, "0af7651916cd43dd8448eb211c80319c");

        Assert.Equal("V4", r.Version);
        Assert.Equal("Answered", r.Outcome);
        Assert.Equal(2, r.Attempts);
        Assert.Equal(2000, r.ModelMs);
        Assert.Equal(2100, r.TotalMs);
        Assert.Equal(4, r.QueryMs);
        Assert.Equal(5200, r.InputTokens);
        Assert.Equal(55, r.OutputTokens);
        Assert.Equal(2, r.RowsReturned);
        Assert.False(r.Truncated);
        Assert.Equal(0.0237, r.EstimatedCost);
        Assert.Equal("order-status", r.KnowledgeIds);
        Assert.Equal("0af7651916cd43dd8448eb211c80319c", r.TraceId);
    }

    [Fact]
    public void A_declined_question_has_no_rows_tokens_or_query_time()
    {
        var result = new AskResult("Average salary?", AgentVersion.V3, "m", AskOutcome.NotAnswerable, null, null, null,
            "Salaries are confidential.", [new AgentAttempt(1, "CANNOT_ANSWER", null, [], TimeSpan.FromMilliseconds(500), null, null)],
            null, TimeSpan.FromMilliseconds(510));

        var r = QueryAuditRecord.From(result, AskedAt, null);

        Assert.Equal("NotAnswerable", r.Outcome);
        Assert.Null(r.RowsReturned);
        Assert.Null(r.QueryMs);
        Assert.Null(r.InputTokens);
        Assert.Null(r.KnowledgeIds);
        Assert.Equal("Salaries are confidential.", r.Message);
    }

    [Fact]
    public void An_infrastructure_failure_is_an_Error_outcome()
    {
        var r = QueryAuditRecord.ForError("Anything?", AgentVersion.V4, "qwen2.5-coder:7b", "Ollama is not reachable.", TimeSpan.FromSeconds(2), AskedAt, null);

        Assert.Equal("Error", r.Outcome);
        Assert.Equal(0, r.Attempts);
        Assert.Equal(2000, r.TotalMs);
    }
}
