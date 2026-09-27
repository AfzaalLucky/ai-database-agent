using AiDatabaseAgent.Infrastructure.Knowledge;
using Microsoft.Extensions.Logging.Abstractions;
using OllamaSharp;
using Xunit.Abstractions;

namespace AiDatabaseAgent.IntegrationTests.Knowledge;

/// <summary>
/// Retrieval recall on paraphrases written AFTER the glossary and its aliases were finalised, and never used
/// to tune them (the probes in <see cref="QdrantKnowledgeStoreTests"/> were). Reports recall rather than
/// asserting each case, and guards against regressions with the floor measured when the set was created.
/// </summary>
public sealed class RetrievalHoldoutTests(ITestOutputHelper output) : IAsyncLifetime, IDisposable
{
    private static readonly (string Question, string Expected)[] Probes =
    [
        ("What's our typical spend per order among small businesses?", "average-order-value"),
        ("Compare the previous calendar month with the one before it", "calendar-periods"),
        ("How profitable was the Power Tools line?", "gross-margin"),
        ("Which clients still owe us money?", "amount-due"),
        ("How long does it take to dispatch orders from Rotterdam?", "fulfilment-time"),
        ("How many people work in Finance?", "employees"),
        ("What did we pay for Facebook ads in 2025?", "not-tracked-marketing"),
        ("Give me the mobile numbers of customers in Canada", "not-tracked-contacts"),
        ("What percentage of shipped orders came back?", "return-rate"),
        ("Which suppliers take the longest to deliver?", "suppliers"),
        ("How many customers have stopped buying from us?", "active-customer"),
        ("What is the worth of the goods sitting in Leeds?", "inventory-value"),
    ];

    /// <summary>Recall measured when this set was written (2026-09-27); a drop below it is a regression.</summary>
    private const int RecallFloor = 10;

    private readonly string _collection = $"foundrytrade_knowledge_holdout_{Guid.NewGuid():N}";
    private QdrantKnowledgeStore _store = null!;

    public Task InitializeAsync()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new KnowledgeOptions { File = Repo.Find("knowledge/foundrytrade.json"), Collection = _collection });
        var embedder = new OllamaApiClient(new HttpClient { BaseAddress = new Uri("http://localhost:11434"), Timeout = TimeSpan.FromMinutes(2) }, "nomic-embed-text");
        _store = new QdrantKnowledgeStore(options, embedder, NullLogger<QdrantKnowledgeStore>.Instance);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (await _store.Client.CollectionExistsAsync(_collection))
            await _store.Client.DeleteCollectionAsync(_collection);
    }

    public void Dispose() => _store?.Dispose();

    [Fact]
    public async Task Recall_on_unseen_paraphrases()
    {
        var found = 0;
        foreach (var (question, expected) in Probes)
        {
            var context = await _store.RetrieveAsync(question);
            var hit = context.Definitions.FirstOrDefault(h => h.Entry.Id == expected);
            if (hit is not null)
                found++;
            output.WriteLine($"{(hit is null ? "MISS" : "hit ")}  {expected,-22} {(hit is null ? "" : hit.MatchedTerm is null ? $"dense {hit.Score:0.00}" : $"term '{hit.MatchedTerm}'"),-24} {question}" +
                (hit is null ? $"  (got: {string.Join(", ", context.Definitions.Select(h => h.Entry.Id))})" : ""));
        }
        output.WriteLine($"recall {found}/{Probes.Length}");
        Assert.True(found >= RecallFloor, $"recall {found}/{Probes.Length} fell below the floor of {RecallFloor}");
    }
}
