using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace AiDatabaseAgent.Core.Knowledge;

[JsonConverter(typeof(JsonStringEnumConverter<KnowledgeKind>))]
public enum KnowledgeKind
{
    /// <summary>A business definition, metric formula or convention ("revenue excludes cancelled orders").</summary>
    [JsonStringEnumMemberName("definition")]
    Definition,

    /// <summary>Data that does not exist in the database — lets the model decline instead of inventing.</summary>
    [JsonStringEnumMemberName("not-tracked")]
    NotTracked,

    /// <summary>A verified question and SQL pair, shown to the model as a worked example.</summary>
    [JsonStringEnumMemberName("example")]
    Example,
}

/// <summary>One entry of the business knowledge base (V4), as stored in knowledge/*.json.</summary>
/// <param name="Aliases">"Also known as" terms — the words people actually use (e.g. "AOV", "earn", "main category").
/// Embedded with the entry and matched literally by <see cref="GlossaryMatcher"/>.</param>
public sealed record KnowledgeEntry(string Id, KnowledgeKind Kind, string Title, string Text, string? Sql = null, string? Question = null, IReadOnlyList<string>? Aliases = null)
{
    /// <summary>The text that is embedded for retrieval.</summary>
    public string SearchText => Kind == KnowledgeKind.Example
        ? $"{Title}\n{Question}\n{Text}"
        : Aliases is { Count: > 0 }
            ? $"{Title}\nAlso called: {string.Join(", ", Aliases)}\n{Text}"
            : $"{Title}\n{Text}";

    /// <summary>Changes whenever anything the prompt or the index depends on changes (drives re-indexing).</summary>
    public string ContentHash => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u001f', Id, Kind, Title, Text, Sql, Question, string.Join('|', Aliases ?? [])))))[..16];
}

public sealed record KnowledgeBase(string Description, IReadOnlyList<KnowledgeEntry> Entries);

/// <param name="Score">Cosine similarity for dense hits; 1 for a literal glossary term match.</param>
/// <param name="MatchedTerm">For lexical hits, the title or alias found in the question.</param>
public sealed record KnowledgeHit(KnowledgeEntry Entry, double Score, string? MatchedTerm = null);

/// <summary>What retrieval found for one question.</summary>
public sealed record KnowledgeContext(IReadOnlyList<KnowledgeHit> Definitions, IReadOnlyList<KnowledgeHit> Examples)
{
    public static readonly KnowledgeContext Empty = new([], []);

    public IEnumerable<KnowledgeHit> All => Definitions.Concat(Examples);
}

public interface IKnowledgeRetriever
{
    /// <summary>Finds the definitions, not-tracked notes and examples most relevant to a question.</summary>
    Task<KnowledgeContext> RetrieveAsync(string question, CancellationToken cancellationToken = default);
}
