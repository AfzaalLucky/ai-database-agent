using AiDatabaseAgent.Core.Agent;
using AiDatabaseAgent.Core.Execution;
using AiDatabaseAgent.Core.Knowledge;
using AiDatabaseAgent.Core.Prompting;
using AiDatabaseAgent.Core.Schema;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiDatabaseAgent.Core.Tests.Prompting;

public class KnowledgePromptTests
{
    private static readonly DatabaseSchema Schema = new("Shop",
        [new SchemaTable("sales", "Orders", TableKind.Table, null, [new("OrderId", "int", false, true, false, null, [])], ["OrderId"], [])]);

    private static readonly DateTimeOffset Now = new(2026, 9, 27, 0, 0, 0, TimeSpan.Zero);

    private static readonly KnowledgeContext Knowledge = new(
        [
            new KnowledgeHit(new KnowledgeEntry("aov", KnowledgeKind.Definition, "Average order value", "Average of order totals,\nnot lines.", "SELECT AVG(t.x) FROM t"), 0.8),
            new KnowledgeHit(new KnowledgeEntry("web", KnowledgeKind.NotTracked, "Not tracked: website analytics", "No sessions or conversion rates."), 0.7),
        ],
        [new KnowledgeHit(new KnowledgeEntry("ex1", KnowledgeKind.Example, "Margin", "Gross margin per subcategory.", "SELECT 1 AS Margin", "What was the margin?"), 0.75)]);

    [Fact]
    public void V4_prompt_lists_definitions_not_tracked_notes_and_patterns()
    {
        var system = SchemaAwarePromptBuilder.Build(Schema, "AOV?", [], [], Now, Knowledge)[0].Text;

        Assert.Contains("Business definitions that apply to this question", system, StringComparison.Ordinal);
        Assert.Contains("- Average order value: Average of order totals, not lines.\n  Pattern: SELECT AVG(t.x) FROM t\n", system, StringComparison.Ordinal);
        Assert.Contains("- NOT TRACKED — Not tracked: website analytics: No sessions or conversion rates.", system, StringComparison.Ordinal);
    }

    [Fact]
    public void Retrieved_examples_follow_the_fixed_ones_just_before_the_question()
    {
        var messages = SchemaAwarePromptBuilder.Build(Schema, "Margin by category?", [], [new FewShotExample("Fixed?", "SELECT 0", "Fixed.")], Now, Knowledge);

        Assert.Equal([ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.User, ChatRole.Assistant, ChatRole.User], messages.Select(m => m.Role));
        Assert.Equal("Fixed?", messages[1].Text);
        Assert.Equal("What was the margin?", messages[3].Text);
        Assert.Equal("Gross margin per subcategory.\n```sql\nSELECT 1 AS Margin\n```", messages[4].Text);
        Assert.Equal("Margin by category?", messages[^1].Text);
    }

    [Fact]
    public void Without_knowledge_the_prompt_is_unchanged()
    {
        var plain = SchemaAwarePromptBuilder.Build(Schema, "Q?", ["note"], [], Now);
        var empty = SchemaAwarePromptBuilder.Build(Schema, "Q?", ["note"], [], Now, KnowledgeContext.Empty);

        Assert.Equal(plain.Select(m => m.Text), empty.Select(m => m.Text));
        Assert.DoesNotContain("Business definitions", plain[0].Text, StringComparison.Ordinal);
    }

    private sealed class NoChat : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "CANNOT_ANSWER: n/a")));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class Schemas : ISchemaProvider
    {
        public Task<DatabaseSchema> GetSchemaAsync(CancellationToken cancellationToken = default) => Task.FromResult(Schema);
    }

    private sealed class NoDb : IQueryExecutor
    {
        public Task<QueryResult> ExecuteAsync(string sql, QueryLimits limits, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<QueryCostEstimate> EstimateAsync(string sql, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FixedRetriever : IKnowledgeRetriever
    {
        public Task<KnowledgeContext> RetrieveAsync(string question, CancellationToken cancellationToken = default) => Task.FromResult(Knowledge);
    }

    private static TextToSqlAgent Agent(IKnowledgeRetriever? retriever) =>
        new(new NoChat(), new Schemas(), new NoDb(), Options.Create(new AgentOptions()), TimeProvider.System, NullLogger<TextToSqlAgent>.Instance, retriever);

    [Fact]
    public async Task V4_without_a_knowledge_base_is_a_clear_error()
    {
        var ex = await Assert.ThrowsAsync<NotSupportedException>(() => Agent(null).AskAsync("Q?", AgentVersion.V4));

        Assert.Contains("knowledge base", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V4_result_carries_the_knowledge_it_used_and_earlier_versions_none()
    {
        var agent = Agent(new FixedRetriever());

        Assert.Equal(2, (await agent.AskAsync("Q?", AgentVersion.V4)).Knowledge!.Definitions.Count);
        Assert.Null((await agent.AskAsync("Q?", AgentVersion.V3)).Knowledge);
    }
}
