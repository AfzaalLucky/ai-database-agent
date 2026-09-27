using System.Diagnostics;
using System.Diagnostics.Metrics;
using AiDatabaseAgent.Core.Agent;
using AiDatabaseAgent.Core.Execution;
using AiDatabaseAgent.Core.Observability;
using AiDatabaseAgent.Core.Schema;
using AiDatabaseAgent.Core.Validation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiDatabaseAgent.Core.Tests.Observability;

/// <summary>
/// The agent's spans and metrics, observed with the BCL listeners an OpenTelemetry SDK uses. Each test runs under
/// its own parent activity and only looks at spans in that trace, so tests running in parallel don't interfere.
/// </summary>
public sealed class AgentTelemetryTests : IDisposable
{
    private static readonly ActivitySource TestSource = new("AiDatabaseAgent.Tests");

    private static readonly DatabaseSchema Schema = new("FoundryTrade",
    [
        new SchemaTable("sales", "Orders", TableKind.Table, null,
            [new SchemaColumn("OrderId", "int", false, false, false, null, []), new SchemaColumn("Status", "varchar", false, false, false, null, [])],
            ["OrderId"], []),
    ]);

    private readonly List<Activity> _spans = [];
    private readonly List<(string Instrument, double Value, Dictionary<string, object?> Tags)> _measurements = [];
    private readonly ActivityListener _activities;
    private readonly MeterListener _meters = new();
    private readonly Lock _gate = new();

    public AgentTelemetryTests()
    {
        _activities = new ActivityListener
        {
            ShouldListenTo = s => s.Name is AgentTelemetry.SourceName or "AiDatabaseAgent.Tests",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a =>
            {
                lock (_gate)
                    _spans.Add(a);
            },
        };
        ActivitySource.AddActivityListener(_activities);

        _meters.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == AgentTelemetry.SourceName)
                listener.EnableMeasurementEvents(instrument);
        };
        _meters.SetMeasurementEventCallback<long>((i, v, tags, _) => Record(i, v, tags));
        _meters.SetMeasurementEventCallback<int>((i, v, tags, _) => Record(i, v, tags));
        _meters.SetMeasurementEventCallback<double>((i, v, tags, _) => Record(i, v, tags));
        _meters.Start();
    }

    private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var copy = new Dictionary<string, object?>();
        foreach (var t in tags)
            copy[t.Key] = t.Value;
        lock (_gate)
            _measurements.Add((instrument.Name, value, copy));
    }

    public void Dispose()
    {
        _activities.Dispose();
        _meters.Dispose();
    }

    [Fact]
    public async Task An_answered_question_is_one_trace_with_a_span_per_pipeline_stage()
    {
        var chat = new ScriptedChat("```sql\nSELECT Status FROM sales.Orders\n```");

        var (result, spans) = await AskTraced(chat, "Order statuses?", AgentVersion.V3);

        Assert.Equal(AskOutcome.Answered, result.Outcome);
        var ask = Assert.Single(spans, s => s.OperationName == "agent.ask");
        Assert.Equal("V3", ask.GetTagItem("agent.version"));
        Assert.Equal("Answered", ask.GetTagItem("agent.outcome"));
        Assert.Equal(1, ask.GetTagItem("agent.attempts"));
        Assert.Equal(2, ask.GetTagItem("db.response.returned_rows"));
        Assert.Null(ask.GetTagItem("agent.question")); // question text never goes on spans

        foreach (var stage in new[] { "agent.validate", "db.estimate", "db.execute" })
            Assert.Equal(ask.SpanId, Assert.Single(spans, s => s.OperationName == stage).ParentSpanId);
        var execute = spans.Single(s => s.OperationName == "db.execute");
        Assert.Equal(ActivityKind.Client, execute.Kind);
        Assert.Equal("SELECT Status FROM sales.Orders", execute.GetTagItem("db.query.text"));
    }

    [Fact]
    public async Task A_repaired_query_records_the_validator_issue_and_two_attempts()
    {
        var chat = new ScriptedChat(
            "```sql\nSELECT Salary FROM sales.Orders\n```",
            "```sql\nSELECT Status FROM sales.Orders\n```");

        var (result, spans) = await AskTraced(chat, "Statuses, repaired?", AgentVersion.V3);

        Assert.Equal(AskOutcome.Answered, result.Outcome);
        var validations = spans.Where(s => s.OperationName == "agent.validate").ToList();
        Assert.Equal([false, true], validations.Select(v => (bool)v.GetTagItem("agent.validation.valid")!));
        Assert.Contains("Salary", (string)validations[0].GetTagItem("agent.validation.issues")!, StringComparison.Ordinal);

        lock (_gate)
        {
            Assert.Contains(_measurements, m => m.Instrument == "agent.validation.issues"
                && (string?)m.Tags["agent.issue.kind"] == nameof(IssueKind.UnknownColumn) && m.Tags["agent.security"] is false);
            Assert.Contains(_measurements, m => m.Instrument == "agent.attempts" && m.Value == 2 && (string?)m.Tags["agent.version"] == "V3");
        }
    }

    [Fact]
    public async Task A_declined_question_is_counted_by_outcome_and_is_not_an_error_span()
    {
        var chat = new ScriptedChat("CANNOT_ANSWER: Salaries are not available.");

        var (result, spans) = await AskTraced(chat, "Average salary?", AgentVersion.V2);

        Assert.Equal(AskOutcome.NotAnswerable, result.Outcome);
        var ask = spans.Single(s => s.OperationName == "agent.ask");
        Assert.NotEqual(ActivityStatusCode.Error, ask.Status);
        Assert.DoesNotContain(spans, s => s.OperationName == "db.execute");
        lock (_gate)
        {
            Assert.Contains(_measurements, m => m.Instrument == "agent.questions"
                && (string?)m.Tags["agent.version"] == "V2" && (string?)m.Tags["agent.outcome"] == "NotAnswerable");
            Assert.Contains(_measurements, m => m.Instrument == "agent.question.duration" && (string?)m.Tags["agent.outcome"] == "NotAnswerable");
        }
    }

    [Fact]
    public async Task A_database_failure_marks_the_execute_span_with_the_error_number()
    {
        var chat = new ScriptedChat("```sql\nSELECT Status FROM sales.Orders\n```");
        var db = new FakeExecutor { Fail = new QueryExecutionException("Execution Timeout Expired.", -2) };

        var (result, spans) = await AskTraced(chat, "Slow statuses?", AgentVersion.V3, db);

        Assert.Equal(AskOutcome.Failed, result.Outcome);
        var execute = spans.Single(s => s.OperationName == "db.execute");
        Assert.Equal(ActivityStatusCode.Error, execute.Status);
        Assert.Equal("-2", execute.GetTagItem("db.response.status_code"));
        Assert.Equal(ActivityStatusCode.Error, spans.Single(s => s.OperationName == "agent.ask").Status);
    }

    [Fact]
    public async Task An_unreachable_model_is_recorded_as_an_error_outcome_and_rethrown()
    {
        var chat = new ScriptedChat { Throw = new HttpRequestException("Connection refused") };

        using var parent = TestSource.StartActivity("test")!;
        await Assert.ThrowsAsync<HttpRequestException>(() => Agent(chat, new FakeExecutor()).AskAsync("Anything?", AgentVersion.V3));
        parent.Stop();

        var ask = SpansOf(parent).Single(s => s.OperationName == "agent.ask");
        Assert.Equal("Error", ask.GetTagItem("agent.outcome"));
        Assert.Equal(ActivityStatusCode.Error, ask.Status);
    }

    private async Task<(AskResult Result, List<Activity> Spans)> AskTraced(IChatClient chat, string question, AgentVersion version, FakeExecutor? db = null)
    {
        using var parent = TestSource.StartActivity("test")!;
        var result = await Agent(chat, db ?? new FakeExecutor()).AskAsync(question, version);
        parent.Stop();
        return (result, SpansOf(parent));
    }

    private List<Activity> SpansOf(Activity parent)
    {
        lock (_gate)
            return _spans.Where(s => s.TraceId == parent.TraceId && s != parent).ToList();
    }

    private static TextToSqlAgent Agent(IChatClient chat, IQueryExecutor db) => new(
        chat,
        new StaticSchema(),
        db,
        Options.Create(new AgentOptions()),
        TimeProvider.System,
        NullLogger<TextToSqlAgent>.Instance);

    private sealed class StaticSchema : ISchemaProvider
    {
        public Task<DatabaseSchema> GetSchemaAsync(CancellationToken cancellationToken = default) => Task.FromResult(Schema);
    }

    private sealed class ScriptedChat(params string[] replies) : IChatClient
    {
        private int _next;

        public Exception? Throw { get; init; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (Throw is not null)
                throw Throw;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, replies[_next++]))
            {
                Usage = new UsageDetails { InputTokenCount = 100, OutputTokenCount = 20 },
            });
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class FakeExecutor : IQueryExecutor
    {
        public QueryExecutionException? Fail { get; init; }

        public Task<QueryResult> ExecuteAsync(string sql, QueryLimits limits, CancellationToken cancellationToken = default) =>
            Fail is not null
                ? throw Fail
                : Task.FromResult(new QueryResult([new ResultColumn("Status", "varchar")], [["Shipped"], ["Pending"]], false, TimeSpan.FromMilliseconds(3)));

        public Task<QueryCostEstimate> EstimateAsync(string sql, CancellationToken cancellationToken = default) => Task.FromResult(new QueryCostEstimate(0.02, 2));
    }
}
