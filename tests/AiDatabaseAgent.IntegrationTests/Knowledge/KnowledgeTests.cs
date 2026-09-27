using System.Text.Json;
using AiDatabaseAgent.Core.Execution;
using AiDatabaseAgent.Core.Knowledge;
using AiDatabaseAgent.Core.Validation;
using AiDatabaseAgent.Infrastructure.Knowledge;
using AiDatabaseAgent.Infrastructure.SqlServer;
using Microsoft.Extensions.Logging.Abstractions;
using OllamaSharp;

namespace AiDatabaseAgent.IntegrationTests.Knowledge;

internal static class Repo
{
    public static string Find(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate))
                return candidate;
        }
        throw new FileNotFoundException(relative);
    }

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static KnowledgeBase LoadKnowledge() =>
        JsonSerializer.Deserialize<KnowledgeBase>(File.ReadAllText(Find("knowledge/foundrytrade.json")), Web)!;
}

/// <summary>The knowledge base is part of the prompt: every SQL snippet in it must be valid, safe and runnable.</summary>
[Collection(FoundryTradeCollection.Name)]
public class KnowledgeBaseIntegrityTests(FoundryTradeFixture db)
{
    [Fact]
    public void Ids_are_unique_and_examples_are_complete()
    {
        var kb = Repo.LoadKnowledge();

        Assert.Equal(kb.Entries.Count, kb.Entries.Select(e => e.Id).Distinct().Count());
        Assert.All(kb.Entries.Where(e => e.Kind == KnowledgeKind.Example), e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Question));
            Assert.False(string.IsNullOrWhiteSpace(e.Sql));
        });
        Assert.Contains(kb.Entries, e => e.Kind == KnowledgeKind.NotTracked);
    }

    [Fact]
    public void No_benchmark_question_leaks_into_the_knowledge_base()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Repo.Find("eval/questions.json")));
        var benchmark = doc.RootElement.GetProperty("questions").EnumerateArray()
            .Select(q => q.GetProperty("question").GetString()!.Trim().TrimEnd('?', '.').ToUpperInvariant())
            .ToHashSet();

        var leaked = Repo.LoadKnowledge().Entries
            .Where(e => e.Question is not null && benchmark.Contains(e.Question.Trim().TrimEnd('?', '.').ToUpperInvariant()))
            .Select(e => e.Id);
        Assert.Empty(leaked);
    }

    [Fact]
    public async Task Every_sql_snippet_passes_validation_and_runs_as_the_agent()
    {
        using var provider = new SqlServerSchemaProvider(db.Options, TimeProvider.System);
        var schema = await provider.GetSchemaAsync();
        var executor = new SqlServerQueryExecutor(db.Options);

        foreach (var e in Repo.LoadKnowledge().Entries.Where(e => e.Sql is not null))
        {
            var validation = SqlValidator.Validate(e.Sql!, schema);
            Assert.True(validation.IsValid, $"{e.Id}: {string.Join(" | ", validation.Issues.Select(i => i.Message))}");
            var result = await executor.ExecuteAsync(e.Sql!, new QueryLimits(5, 30));
            Assert.NotEmpty(result.Columns);
        }
    }
}

/// <summary>Indexing and retrieval against the docker-compose Qdrant and Ollama's nomic-embed-text.</summary>
public sealed class QdrantKnowledgeStoreTests : IAsyncLifetime, IDisposable
{
    private readonly string _collection = $"foundrytrade_knowledge_test_{Guid.NewGuid():N}";
    private QdrantKnowledgeStore _store = null!;
    private string _file = null!;

    public Task InitializeAsync()
    {
        _file = Path.Combine(Path.GetTempPath(), $"{_collection}.json");
        File.Copy(Repo.Find("knowledge/foundrytrade.json"), _file);
        var options = Microsoft.Extensions.Options.Options.Create(new KnowledgeOptions { File = _file, Collection = _collection });
        var embedder = new OllamaApiClient(new HttpClient { BaseAddress = new Uri("http://127.0.0.1:11434"), Timeout = TimeSpan.FromMinutes(2) }, "nomic-embed-text");
        _store = new QdrantKnowledgeStore(options, embedder, NullLogger<QdrantKnowledgeStore>.Instance);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (await _store.Client.CollectionExistsAsync(_collection))
            await _store.Client.DeleteCollectionAsync(_collection);
        File.Delete(_file);
    }

    public void Dispose() => _store?.Dispose();

    [Fact]
    public async Task Indexing_is_incremental_and_idempotent()
    {
        var total = Repo.LoadKnowledge().Entries.Count;

        var first = await _store.IndexAsync();
        Assert.Equal(new IndexReport(total, 0, 0, 0, false), first);
        Assert.Equal((ulong)total, await _store.Client.CountAsync(_collection));

        var second = await _store.IndexAsync();
        Assert.Equal(new IndexReport(0, 0, 0, total, false), second);

        // Edit one entry and remove another: one update, one delete, the rest untouched.
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(_file));
        var entries = doc.RootElement.GetProperty("entries").EnumerateArray().Select(e => JsonSerializer.Deserialize<Dictionary<string, object>>(e.GetRawText())!).ToList();
        entries[0]["text"] = "Changed text.";
        entries.RemoveAt(entries.Count - 1);
        await File.WriteAllTextAsync(_file, JsonSerializer.Serialize(new { description = "test", entries }));

        var third = await _store.IndexAsync();
        Assert.Equal(new IndexReport(0, 1, 1, total - 2, false), third);
        Assert.Equal((ulong)(total - 1), await _store.Client.CountAsync(_collection));
    }

    [Theory]
    [InlineData("What is the average order value for enterprise customers?", "average-order-value")]
    [InlineData("How many orders did we get last month?", "calendar-periods")]
    [InlineData("What is the conversion rate of our online shop?", "not-tracked-web")]
    [InlineData("Show revenue for each main product category", "category-rollup")]
    [InlineData("Which products are below their reorder point?", "stock-levels")]
    [InlineData("How much do our warehouse staff earn?", "not-tracked-pay")]
    public async Task Retrieves_the_relevant_definition(string question, string expectedId)
    {
        var context = await _store.RetrieveAsync(question);

        Assert.Contains(context.Definitions, h => h.Entry.Id == expectedId);
    }

    [Fact]
    public async Task Examples_are_only_returned_when_close_enough()
    {
        var close = await _store.RetrieveAsync("What was the gross margin percentage by product subcategory in 2025?");
        var unrelated = await _store.RetrieveAsync("What is the capital of France?");

        Assert.Contains(close.Examples, h => h.Entry.Id == "ex-margin-by-category");
        Assert.Empty(unrelated.Examples);
    }
}
