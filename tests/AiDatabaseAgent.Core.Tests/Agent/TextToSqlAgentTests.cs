using AiDatabaseAgent.Core.Agent;
using AiDatabaseAgent.Core.Execution;
using AiDatabaseAgent.Core.Schema;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiDatabaseAgent.Core.Tests.Agent;

public class TextToSqlAgentTests
{
    private static SchemaColumn Col(string name) => new(name, "int", false, false, false, null, []);

    private static readonly DatabaseSchema Schema = new("FoundryTrade",
    [
        new SchemaTable("sales", "Orders", TableKind.Table, null, [Col("OrderId"), Col("Status")], ["OrderId"], []),
        new SchemaTable("hr", "EmployeeDirectory", TableKind.View, null, [Col("EmployeeId"), Col("DepartmentId")], ["EmployeeId"], []),
        new SchemaTable("hr", "Departments", TableKind.Table, null, [Col("DepartmentId"), Col("Name")], ["DepartmentId"], []),
        new SchemaTable("catalog", "Products", TableKind.Table, null, [Col("ProductId"), Col("Sku")], ["ProductId"], []),
        new SchemaTable("inventory", "Stock", TableKind.Table, null, [Col("ProductId"), Col("QuantityOnHand"), Col("ReorderLevel")], [], []),
    ]);

    [Fact]
    public async Task V3_does_not_accept_a_refusal_right_after_a_syntax_error()
    {
        // Live regression: "ORDER BY ... TOP (5)" (LIMIT-style) failed to parse, then the model declined.
        var chat = new ScriptedChat(
            Sql("SELECT Status FROM sales.Orders ORDER BY Status TOP (5)"),
            "CANNOT_ANSWER: The query was not accepted due to a syntax error.",
            Sql("SELECT TOP (5) Status FROM sales.Orders ORDER BY Status"));
        var db = new FakeExecutor();

        var r = await Agent(chat, db).AskAsync("Top 5 statuses?", AgentVersion.V3);

        Assert.Equal(AskOutcome.Answered, r.Outcome);
        Assert.Equal("Declined after a fixable error.", r.Attempts[1].Issues[0]);
        Assert.Contains("not missing data", chat.Requests[2][^1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V3_does_not_accept_a_refusal_the_validator_can_contradict()
    {
        // Live regression: p.ReorderLevel instead of s.ReorderLevel, then "CANNOT_ANSWER" despite the hint.
        var chat = new ScriptedChat(
            Sql("SELECT p.Sku FROM catalog.Products p JOIN inventory.Stock s ON s.ProductId = p.ProductId WHERE s.QuantityOnHand < p.ReorderLevel"),
            "CANNOT_ANSWER: ReorderLevel is not on catalog.Products.",
            Sql("SELECT p.Sku FROM catalog.Products p JOIN inventory.Stock s ON s.ProductId = p.ProductId WHERE s.QuantityOnHand < s.ReorderLevel"));
        var db = new FakeExecutor();

        var r = await Agent(chat, db).AskAsync("Products below reorder level?", AgentVersion.V3);

        Assert.Equal(AskOutcome.Answered, r.Outcome);
        Assert.Equal(3, r.Attempts.Count);
        Assert.StartsWith("Declined, but the data does exist (inventory.Stock (alias s))", r.Attempts[1].Issues[0], StringComparison.Ordinal);
        Assert.Contains("DOES exist", chat.Requests[2][^1].Text, StringComparison.Ordinal);
        Assert.Single(db.Executed);
    }

    private sealed class ScriptedChat(params string[] replies) : IChatClient
    {
        private int _next;

        public List<List<ChatMessage>> Requests { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Requests.Add(messages.ToList());
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, replies[_next++])));
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
        public List<string> Executed { get; } = [];

        public Func<string, Exception?> Fail { get; set; } = _ => null;

        public double Cost { get; set; } = 0.5;

        public Task<QueryResult> ExecuteAsync(string sql, QueryLimits limits, CancellationToken cancellationToken = default)
        {
            Executed.Add(sql);
            if (Fail(sql) is { } ex)
                throw ex;
            return Task.FromResult(new QueryResult([new ResultColumn("n", "int")], [[1]], false, TimeSpan.FromMilliseconds(3)));
        }

        public Task<QueryCostEstimate> EstimateAsync(string sql, CancellationToken cancellationToken = default) =>
            Task.FromResult(new QueryCostEstimate(Cost, 1));
    }

    private sealed class FixedSchema : ISchemaProvider
    {
        public Task<DatabaseSchema> GetSchemaAsync(CancellationToken cancellationToken = default) => Task.FromResult(Schema);
    }

    private static TextToSqlAgent Agent(IChatClient chat, IQueryExecutor executor, int maxRepairs = 2) =>
        new(chat, new FixedSchema(), executor,
            Options.Create(new AgentOptions { MaxRepairAttempts = maxRepairs, MaxEstimatedCost = 100 }),
            TimeProvider.System, NullLogger<TextToSqlAgent>.Instance);

    private static string Sql(string q) => $"Returns it.\n```sql\n{q}\n```";

    [Fact]
    public async Task V3_feeds_an_unknown_column_back_and_accepts_a_refusal()
    {
        var chat = new ScriptedChat(
            Sql("SELECT d.Name, AVG(e.Salary) FROM hr.EmployeeDirectory e JOIN hr.Departments d ON d.DepartmentId = e.DepartmentId GROUP BY d.Name"),
            "CANNOT_ANSWER: salary data is not available");
        var db = new FakeExecutor();

        var r = await Agent(chat, db).AskAsync("Average salary per department?", AgentVersion.V3);

        Assert.Equal(AskOutcome.NotAnswerable, r.Outcome);
        Assert.Equal("salary data is not available", r.Message);
        Assert.Equal(2, r.Attempts.Count);
        Assert.Contains("Column 'Salary' does not exist on hr.EmployeeDirectory", r.Attempts[0].Issues[0], StringComparison.Ordinal);
        Assert.Empty(db.Executed); // the invented column never reached the database
        var repair = chat.Requests[1][^1].Text;
        Assert.Contains("Its columns are: EmployeeId, DepartmentId", repair, StringComparison.Ordinal);
        Assert.Contains("CANNOT_ANSWER", repair, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V3_never_executes_a_write_and_repairs_to_a_select()
    {
        var chat = new ScriptedChat(Sql("DELETE FROM sales.Orders WHERE Status = 'Cancelled'"), Sql("SELECT COUNT(*) AS n FROM sales.Orders WHERE Status = 'Cancelled'"));
        var db = new FakeExecutor();

        var r = await Agent(chat, db).AskAsync("Cancelled orders", AgentVersion.V3);

        Assert.Equal(AskOutcome.Answered, r.Outcome);
        Assert.Equal(["SELECT COUNT(*) AS n FROM sales.Orders WHERE Status = 'Cancelled'"], db.Executed);
    }

    [Fact]
    public async Task V3_gives_up_after_the_repair_budget_and_reports_rejected()
    {
        var bad = Sql("SELECT * FROM master.sys.sql_logins");
        var chat = new ScriptedChat(bad, bad, bad);
        var db = new FakeExecutor();

        var r = await Agent(chat, db, maxRepairs: 2).AskAsync("Logins?", AgentVersion.V3);

        Assert.Equal(AskOutcome.Rejected, r.Outcome);
        Assert.Equal(3, r.Attempts.Count);
        Assert.Empty(db.Executed);
    }

    [Fact]
    public async Task V3_repairs_after_a_database_error()
    {
        var chat = new ScriptedChat(Sql("SELECT OrderId / 0 AS n FROM sales.Orders"), Sql("SELECT COUNT(*) AS n FROM sales.Orders"));
        var db = new FakeExecutor { Fail = sql => sql.Contains("/ 0", StringComparison.Ordinal) ? new QueryExecutionException("Divide by zero error encountered.", 8134) : null };

        var r = await Agent(chat, db).AskAsync("How many orders?", AgentVersion.V3);

        Assert.Equal(AskOutcome.Answered, r.Outcome);
        Assert.Equal(2, db.Executed.Count);
        Assert.Contains("Divide by zero", chat.Requests[1][^1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V3_refuses_queries_over_the_cost_limit_without_running_them()
    {
        var chat = new ScriptedChat(Sql("SELECT COUNT(*) AS n FROM sales.Orders a CROSS JOIN sales.Orders b"));
        var db = new FakeExecutor { Cost = 5000 };

        var r = await Agent(chat, db).AskAsync("Everything times everything", AgentVersion.V3);

        Assert.Equal(AskOutcome.Rejected, r.Outcome);
        Assert.Equal(5000, r.Estimate!.EstimatedCost);
        Assert.Empty(db.Executed);
    }

    [Fact]
    public async Task V3_does_not_retry_timeouts()
    {
        var chat = new ScriptedChat(Sql("SELECT COUNT(*) AS n FROM sales.Orders"), Sql("SELECT 1 AS n"));
        var db = new FakeExecutor { Fail = _ => new QueryExecutionException("The query timed out and was cancelled.", -2) };

        var r = await Agent(chat, db).AskAsync("How many orders?", AgentVersion.V3);

        Assert.Equal(AskOutcome.Failed, r.Outcome);
        Assert.Single(r.Attempts);
    }

    [Fact]
    public async Task V2_runs_the_model_sql_unvalidated_and_does_not_repair()
    {
        var chat = new ScriptedChat(Sql("SELECT AVG(e.Salary) FROM hr.EmployeeDirectory e"));
        var db = new FakeExecutor { Fail = _ => new QueryExecutionException("Invalid column name 'Salary'.", 207) };

        var r = await Agent(chat, db).AskAsync("Average salary?", AgentVersion.V2);

        Assert.Equal(AskOutcome.Failed, r.Outcome);
        Assert.Single(db.Executed); // the database was the only thing that caught it
        Assert.Single(r.Attempts);
    }
}
