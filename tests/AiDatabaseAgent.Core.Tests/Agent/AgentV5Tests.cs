using AiDatabaseAgent.Core.Agent;
using AiDatabaseAgent.Core.Execution;
using AiDatabaseAgent.Core.Knowledge;
using AiDatabaseAgent.Core.Schema;
using AiDatabaseAgent.Core.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiDatabaseAgent.Core.Tests.Agent;

/// <summary>
/// V5 with a scripted model that issues real (structured) tool calls: FunctionInvokingChatClient executes the
/// real <see cref="AgentToolbox"/> against a fake database, so the whole loop is exercised deterministically.
/// </summary>
public class AgentV5Tests
{
    private static SchemaColumn Col(string name, params string[] allowed) => new(name, "int", false, false, false, null, allowed);

    private static readonly DatabaseSchema Schema = new("FoundryTrade",
    [
        new SchemaTable("catalog", "Products", TableKind.Table, "Products we sell.", [Col("ProductId"), Col("Name")], ["ProductId"], []),
        new SchemaTable("sales", "Orders", TableKind.Table, "Sales orders. Revenue excludes cancelled.", [Col("OrderId"), Col("Status", "Cancelled", "Shipped")], ["OrderId"], []),
    ]);

    /// <summary>Each reply is either a list of tool calls or final text.</summary>
    private sealed class ScriptedToolChat(params object[] replies) : IChatClient
    {
        private int _next;

        public List<List<ChatMessage>> Requests { get; } = [];

        public List<ChatOptions?> Options { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Requests.Add(messages.ToList());
            Options.Add(options);
            var reply = replies[_next++];
            var message = reply is FunctionCallContent[] calls
                ? new ChatMessage(ChatRole.Assistant, [.. calls])
                : new ChatMessage(ChatRole.Assistant, (string)reply);
            return Task.FromResult(new ChatResponse(message));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class FakeDb : IQueryExecutor
    {
        public List<string> Executed { get; } = [];

        public Task<QueryResult> ExecuteAsync(string sql, QueryLimits limits, CancellationToken cancellationToken = default)
        {
            Executed.Add(sql);
            return Task.FromResult(new QueryResult([new ResultColumn("v", "nvarchar")], [["Cancelled"], ["Shipped"]], false, TimeSpan.Zero));
        }

        public Task<QueryCostEstimate> EstimateAsync(string sql, CancellationToken cancellationToken = default) => Task.FromResult(new QueryCostEstimate(0.1, 2));
    }

    private sealed class Schemas : ISchemaProvider
    {
        public Task<DatabaseSchema> GetSchemaAsync(CancellationToken cancellationToken = default) => Task.FromResult(Schema);
    }

    private sealed class Glossary(bool notTracked) : IKnowledgeRetriever
    {
        public Task<KnowledgeContext> RetrieveAsync(string question, CancellationToken cancellationToken = default) =>
            Task.FromResult(new KnowledgeContext(
                [new KnowledgeHit(new KnowledgeEntry("x", notTracked ? KnowledgeKind.NotTracked : KnowledgeKind.Definition, notTracked ? "Not available: salaries" : "Revenue", "Definition text."), 0.9)],
                []));
    }

    private static TextToSqlAgent Agent(IChatClient chat, IQueryExecutor db, bool notTracked = false) =>
        new(chat, new Schemas(), db, Options.Create(new AgentOptions { ToolModel = "tool-model" }), TimeProvider.System, NullLogger<TextToSqlAgent>.Instance, new Glossary(notTracked));

    private static FunctionCallContent Call(string id, string name, Dictionary<string, object?> args) => new(id, name, args);

    [Fact]
    public async Task Tools_are_called_and_traced_and_the_final_sql_passes_the_v3_gate()
    {
        var chat = new ScriptedToolChat(
            new[] { Call("1", "describe_table", new() { ["table"] = "sales.Orders" }) },
            new[] { Call("2", "sample_values", new() { ["table"] = "sales.Orders", ["column"] = "Status", ["max"] = 10 }) },
            new[] { Call("3", "run_query", new() { ["sql"] = "SELECT COUNT(*) AS n FROM sales.Orders WHERE Status = 'Cancelled'" }) },
            "Counts cancelled orders.\n```sql\nSELECT COUNT(*) AS n FROM sales.Orders WHERE Status = 'Cancelled'\n```",
            "CONFIRMED");
        var db = new FakeDb();

        var r = await Agent(chat, db).AskAsync("How many cancelled orders?", AgentVersion.V5);

        Assert.Equal(AskOutcome.Answered, r.Outcome);
        Assert.Equal("tool-model", r.Model);
        Assert.Equal("tool-model", chat.Options[0]!.ModelId);
        // The fourth call is the harness's own verification run of the answer.
        Assert.Equal(["describe_table", "sample_values", "run_query", "run_query"], r.ToolCalls!.Select(t => t.Name));
        Assert.Equal("Counts cancelled orders.", r.Explanation);
        Assert.All(r.ToolCalls!, t => Assert.True(t.Succeeded));
        Assert.Contains("TABLE sales.Orders (", r.ToolCalls![0].Result, StringComparison.Ordinal);
        Assert.Equal("Cancelled, Shipped", r.ToolCalls[1].Result);
        // sample_values quoted the names itself; the model's test, the verification run and the final execution ran its SQL.
        Assert.StartsWith("SELECT DISTINCT TOP (10) [Status] FROM [sales].[Orders]", db.Executed[0], StringComparison.Ordinal);
        Assert.Equal(4, db.Executed.Count);
    }

    [Fact]
    public async Task The_self_check_shows_the_model_its_rows_and_accepts_a_correction()
    {
        // "How many active customers?" answered with one row per customer: the preview lets the model see it.
        var chat = new ScriptedToolChat(
            "Lists active customers.\n```sql\nSELECT OrderId FROM sales.Orders\n```",
            "It should be a single count.\n```sql\nSELECT COUNT(*) AS n FROM sales.Orders\n```");
        var db = new FakeDb();

        var r = await Agent(chat, db).AskAsync("How many orders are there?", AgentVersion.V5);

        Assert.Equal(AskOutcome.Answered, r.Outcome);
        Assert.Equal("SELECT COUNT(*) AS n FROM sales.Orders", r.Sql);
        Assert.Contains("I ran your query (run_query). It returned:", chat.Requests[1][^1].Text, StringComparison.Ordinal);
        Assert.Contains("v\nCancelled\nShipped", chat.Requests[1][^1].Text, StringComparison.Ordinal);
        // One self-check per question (bounded cost): the correction goes through the V3 gate and runs, without a second preview.
        Assert.Equal(["run_query"], r.ToolCalls!.Select(t => t.Name));
        Assert.Equal(["SELECT OrderId FROM sales.Orders", "SELECT COUNT(*) AS n FROM sales.Orders"], db.Executed);
    }

    [Fact]
    public async Task Run_query_rejects_unsafe_sql_without_executing_it()
    {
        var chat = new ScriptedToolChat(
            new[] { Call("1", "run_query", new() { ["sql"] = "DELETE FROM sales.Orders" }) },
            "CANNOT_ANSWER: deleting data is not allowed.");
        var db = new FakeDb();

        var r = await Agent(chat, db).AskAsync("Delete all orders", AgentVersion.V5);

        Assert.StartsWith("Rejected:", r.ToolCalls![0].Result, StringComparison.Ordinal);
        Assert.False(r.ToolCalls[0].Succeeded);
        Assert.Empty(db.Executed);
    }

    [Fact]
    public async Task Unknown_tables_and_columns_come_back_as_guidance()
    {
        var chat = new ScriptedToolChat(
            new[] { Call("1", "describe_table", new() { ["table"] = "hr.Employees" }), Call("2", "sample_values", new() { ["table"] = "catalog.Products", ["column"] = "ProductName" }) },
            "CANNOT_ANSWER: no.");
        var r = await Agent(chat, new FakeDb()).AskAsync("Q?", AgentVersion.V5);

        Assert.StartsWith("No table named 'hr.Employees'", r.ToolCalls![0].Result, StringComparison.Ordinal);
        Assert.Equal("No column 'ProductName' on catalog.Products. Its columns are: ProductId, Name.", r.ToolCalls[1].Result);
    }

    [Fact]
    public async Task A_refusal_right_after_a_failed_test_query_is_pushed_back()
    {
        // Live regression: guessed p.ProductName, run_query rejected it, the model declined "not tracked".
        var chat = new ScriptedToolChat(
            new[] { Call("1", "run_query", new() { ["sql"] = "SELECT p.ProductName FROM catalog.Products p" }) },
            "CANNOT_ANSWER: product names are not tracked.",
            new[] { Call("2", "describe_table", new() { ["table"] = "catalog.Products" }) },
            "Lists product names.\n```sql\nSELECT p.Name FROM catalog.Products p\n```",
            "CONFIRMED");
        var db = new FakeDb();

        var r = await Agent(chat, db).AskAsync("List product names", AgentVersion.V5);

        Assert.Equal(AskOutcome.Answered, r.Outcome);
        Assert.Contains(r.Attempts, a => a.Issues.Any(i => i.StartsWith("Declined after a failed test query", StringComparison.Ordinal)));
        Assert.Contains("describe_table", chat.Requests[2][^1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Declining_without_investigating_gets_one_push_back()
    {
        // Live pattern: "Customer count is not tracked" in 1 s, without a single tool call.
        var chat = new ScriptedToolChat(
            "CANNOT_ANSWER: customer count is not tracked.",
            new[] { Call("1", "describe_table", new() { ["table"] = "sales.Orders" }) },
            "Counts orders.\n```sql\nSELECT COUNT(*) AS n FROM sales.Orders\n```",
            "CONFIRMED");

        var r = await Agent(chat, new FakeDb()).AskAsync("How many orders?", AgentVersion.V5);

        Assert.Equal(AskOutcome.Answered, r.Outcome);
        Assert.Equal("Declined without investigating.", r.Attempts[0].Issues[0]);
    }

    [Fact]
    public async Task A_second_refusal_after_investigating_is_accepted()
    {
        var chat = new ScriptedToolChat(
            "CANNOT_ANSWER: website traffic is not stored.",
            new[] { Call("1", "search_glossary", new() { ["query"] = "website traffic" }) },
            "CANNOT_ANSWER: website traffic is not stored.");

        var r = await Agent(chat, new FakeDb()).AskAsync("How many website visits?", AgentVersion.V5);

        Assert.Equal(AskOutcome.NotAnswerable, r.Outcome);
        Assert.Equal(2, r.Attempts.Count);
    }

    [Fact]
    public async Task Full_context_mode_starts_from_the_v4_prompt_and_minimal_mode_from_table_names()
    {
        var full = new ScriptedToolChat("CANNOT_ANSWER: n/a.", "CANNOT_ANSWER: n/a.");
        await Agent(full, new FakeDb()).AskAsync("Q?", AgentVersion.V5);
        var fullSystem = full.Requests[0][0].Text;
        Assert.Contains("TABLE sales.Orders (", fullSystem, StringComparison.Ordinal);
        Assert.Contains("You also have tools", fullSystem, StringComparison.Ordinal);

        var minimal = new ScriptedToolChat("CANNOT_ANSWER: n/a.", "CANNOT_ANSWER: n/a.");
        var agent = new TextToSqlAgent(minimal, new Schemas(), new FakeDb(), Options.Create(new AgentOptions { V5Context = V5Context.Minimal }),
            TimeProvider.System, NullLogger<TextToSqlAgent>.Instance, new Glossary(false));
        await agent.AskAsync("Q?", AgentVersion.V5);
        var minimalSystem = minimal.Requests[0][0].Text;
        Assert.DoesNotContain("TABLE sales.Orders (", minimalSystem, StringComparison.Ordinal);
        Assert.Contains("- sales.Orders\n", minimalSystem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refusal_backed_by_a_not_tracked_glossary_entry_is_accepted()
    {
        var chat = new ScriptedToolChat(
            new[] { Call("1", "search_glossary", new() { ["query"] = "salary" }) },
            new[] { Call("2", "run_query", new() { ["sql"] = "SELECT Salary FROM hr.EmployeeDirectory" }) },
            "CANNOT_ANSWER: salaries are not available.");

        var r = await Agent(chat, new FakeDb(), notTracked: true).AskAsync("Average salary?", AgentVersion.V5);

        Assert.Equal(AskOutcome.NotAnswerable, r.Outcome);
        Assert.Equal("salaries are not available", r.Message);
    }
}
