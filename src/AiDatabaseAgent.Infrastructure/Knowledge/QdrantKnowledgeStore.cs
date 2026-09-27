using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiDatabaseAgent.Core.Knowledge;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace AiDatabaseAgent.Infrastructure.Knowledge;

public sealed class KnowledgeOptions
{
    public const string SectionName = "Knowledge";

    /// <summary>Knowledge base file, relative to the application directory.</summary>
    public string File { get; set; } = "knowledge/foundrytrade.json";

    /// <summary>127.0.0.1 rather than "localhost", which costs ~2 s per new connection on Windows (IPv6 tried first).</summary>
    public string QdrantHost { get; set; } = "127.0.0.1";

    /// <summary>Qdrant gRPC port (the .NET client uses gRPC; 6333 is REST).</summary>
    public int QdrantGrpcPort { get; set; } = 6334;

    public string Collection { get; set; } = "foundrytrade_knowledge";

    /// <summary>Ollama embedding model. nomic-embed-text expects "search_document:"/"search_query:" prefixes.</summary>
    public string EmbeddingModel { get; set; } = "nomic-embed-text";

    public string DocumentPrefix { get; set; } = "search_document: ";

    public string QueryPrefix { get; set; } = "search_query: ";

    /// <summary>Definitions and not-tracked notes added to the prompt per question (lexical + dense).</summary>
    public int DefinitionsTopK { get; set; } = 6;

    /// <summary>At most this many of them come from literal glossary term matches (the rest from dense search).</summary>
    public int MaxLexicalMatches { get; set; } = 4;

    /// <summary>Verified examples added as worked turns per question.</summary>
    public int ExamplesTopK { get; set; } = 2;

    /// <summary>Cosine similarity below which a definition is considered unrelated.</summary>
    public float MinDefinitionScore { get; set; } = 0.55f;

    /// <summary>
    /// Examples are only useful when they are near-duplicates: a loosely related example gets copied
    /// (filters, even clause order). Measured with nomic-embed-text: true paraphrases 0.77–0.88, topical but
    /// different questions up to 0.77 — so 0.80 favours precision.
    /// </summary>
    public float MinExampleScore { get; set; } = 0.80f;
}

public sealed record IndexReport(int Added, int Updated, int Deleted, int Unchanged, bool Recreated);

/// <summary>
/// Business knowledge in Qdrant. Indexing is idempotent and incremental: each entry's content hash is kept
/// in its payload, so only new or changed entries are embedded again, and entries removed from the file
/// are deleted. The collection is (re)created when missing or when the embedding size changes.
/// </summary>
public sealed partial class QdrantKnowledgeStore(
    IOptions<KnowledgeOptions> options,
    [Microsoft.Extensions.DependencyInjection.FromKeyedServices("knowledge")] IEmbeddingGenerator<string, Embedding<float>> embedder,
    ILogger<QdrantKnowledgeStore> logger) : IKnowledgeRetriever, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly KnowledgeOptions _options = options.Value;
    private readonly QdrantClient _qdrant = new(options.Value.QdrantHost, options.Value.QdrantGrpcPort);
    private readonly SemaphoreSlim _indexGate = new(1, 1);
    private volatile bool _indexed;

    public QdrantClient Client => _qdrant;

    /// <summary>Reads the knowledge file (re-read on every call, so edits are picked up by the next index run).</summary>
    public KnowledgeBase Load()
    {
        var path = Path.IsPathRooted(_options.File) ? _options.File : Path.Combine(AppContext.BaseDirectory, _options.File);
        return JsonSerializer.Deserialize<KnowledgeBase>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException($"Knowledge file {path} is empty.");
    }

    /// <summary>Indexes the knowledge file once per process (later calls return immediately).</summary>
    public async Task EnsureIndexedAsync(CancellationToken ct = default)
    {
        if (_indexed)
            return;
        await _indexGate.WaitAsync(ct);
        try
        {
            if (!_indexed)
            {
                var report = await IndexAsync(ct);
                LogIndexed(logger, _options.Collection, report.Added, report.Updated, report.Deleted, report.Unchanged);
                _indexed = true;
            }
        }
        finally
        {
            _indexGate.Release();
        }
    }

    public async Task<IndexReport> IndexAsync(CancellationToken ct = default)
    {
        var entries = Load().Entries;
        var dims = (await EmbedAsync([_options.DocumentPrefix + "probe"], ct))[0].Length;

        var recreated = false;
        if (await _qdrant.CollectionExistsAsync(_options.Collection, ct))
        {
            var info = await _qdrant.GetCollectionInfoAsync(_options.Collection, ct);
            if (info.Config.Params.VectorsConfig.Params.Size != (ulong)dims)
            {
                await _qdrant.DeleteCollectionAsync(_options.Collection, cancellationToken: ct);
                recreated = true;
            }
        }
        if (recreated || !await _qdrant.CollectionExistsAsync(_options.Collection, ct))
        {
            await _qdrant.CreateCollectionAsync(_options.Collection, new VectorParams { Size = (ulong)dims, Distance = Distance.Cosine }, cancellationToken: ct);
            await _qdrant.CreatePayloadIndexAsync(_options.Collection, "kind", PayloadSchemaType.Keyword, cancellationToken: ct);
        }

        // What is already indexed: entry id -> content hash.
        var existing = new Dictionary<string, string>();
        var scroll = await _qdrant.ScrollAsync(_options.Collection, limit: 10_000, payloadSelector: new WithPayloadSelector { Include = new PayloadIncludeSelector { Fields = { "id", "hash" } } }, cancellationToken: ct);
        foreach (var point in scroll.Result)
            existing[point.Payload["id"].StringValue] = point.Payload["hash"].StringValue;

        var changed = entries.Where(e => !existing.TryGetValue(e.Id, out var hash) || hash != e.ContentHash).ToList();
        if (changed.Count > 0)
        {
            var vectors = await EmbedAsync(changed.Select(e => _options.DocumentPrefix + e.SearchText).ToList(), ct);
            var points = changed.Select((e, i) => ToPoint(e, vectors[i])).ToList();
            await _qdrant.UpsertAsync(_options.Collection, points, cancellationToken: ct);
        }

        var current = entries.Select(e => e.Id).ToHashSet();
        var stale = existing.Keys.Where(id => !current.Contains(id)).Select(PointId).ToList();
        if (stale.Count > 0)
            await _qdrant.DeleteAsync(_options.Collection, stale, cancellationToken: ct);

        var added = changed.Count(e => !existing.ContainsKey(e.Id));
        return new IndexReport(added, changed.Count - added, stale.Count, entries.Count - changed.Count, recreated);
    }

    public async Task<KnowledgeContext> RetrieveAsync(string question, CancellationToken cancellationToken = default)
    {
        await EnsureIndexedAsync(cancellationToken);
        var vector = (await EmbedAsync([_options.QueryPrefix + question], cancellationToken))[0];

        async Task<List<KnowledgeHit>> Search(string[] kinds, int limit, float minScore)
        {
            var filter = new Filter();
            filter.Should.AddRange(kinds.Select(k => Conditions.MatchKeyword("kind", k)));
            var hits = await _qdrant.QueryAsync(_options.Collection, query: vector, filter: filter, scoreThreshold: minScore,
                limit: (ulong)limit, payloadSelector: true, cancellationToken: cancellationToken);
            return hits.Select(h => new KnowledgeHit(FromPayload(h.Payload), h.Score)).ToList();
        }

        // Hybrid: literal glossary-term matches first (precise, explainable), then dense hits for paraphrases.
        var lexical = GlossaryMatcher.Find(question, Load().Entries)
            .Take(_options.MaxLexicalMatches)
            .Select(m => new KnowledgeHit(m.Entry, 1.0, m.Term))
            .ToList();
        var dense = await Search(["definition", "not-tracked"], _options.DefinitionsTopK, _options.MinDefinitionScore);
        var definitions = lexical
            .Concat(dense.Where(d => lexical.All(l => l.Entry.Id != d.Entry.Id)))
            .Take(_options.DefinitionsTopK)
            .ToList();

        var examples = await Search(["example"], _options.ExamplesTopK, _options.MinExampleScore);
        return new KnowledgeContext(definitions, examples);
    }

    public void Dispose()
    {
        _qdrant.Dispose();
        _indexGate.Dispose();
    }

    private async Task<float[][]> EmbedAsync(IList<string> texts, CancellationToken ct)
    {
        var result = await embedder.GenerateAsync(texts, cancellationToken: ct);
        return result.Select(e => e.Vector.ToArray()).ToArray();
    }

    private static string KindName(KnowledgeKind kind) => kind switch
    {
        KnowledgeKind.Definition => "definition",
        KnowledgeKind.NotTracked => "not-tracked",
        _ => "example",
    };

    private static PointId PointId(string entryId) =>
        new() { Uuid = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(entryId))[..16]).ToString() };

    private static PointStruct ToPoint(KnowledgeEntry e, float[] vector)
    {
        var point = new PointStruct { Id = PointId(e.Id), Vectors = vector };
        point.Payload["id"] = e.Id;
        point.Payload["kind"] = KindName(e.Kind);
        point.Payload["title"] = e.Title;
        point.Payload["text"] = e.Text;
        point.Payload["hash"] = e.ContentHash;
        if (e.Sql is not null)
            point.Payload["sql"] = e.Sql;
        if (e.Question is not null)
            point.Payload["question"] = e.Question;
        return point;
    }

    private static KnowledgeEntry FromPayload(Google.Protobuf.Collections.MapField<string, Value> p)
    {
        string? Get(string key) => p.TryGetValue(key, out var v) ? v.StringValue : null;
        var kind = Get("kind") switch
        {
            "definition" => KnowledgeKind.Definition,
            "not-tracked" => KnowledgeKind.NotTracked,
            _ => KnowledgeKind.Example,
        };
        return new KnowledgeEntry(Get("id")!, kind, Get("title")!, Get("text")!, Get("sql"), Get("question"));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Knowledge index {Collection}: {Added} added, {Updated} updated, {Deleted} deleted, {Unchanged} unchanged")]
    private static partial void LogIndexed(ILogger logger, string collection, int added, int updated, int deleted, int unchanged);
}
