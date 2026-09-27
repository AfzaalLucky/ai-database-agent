using System.Globalization;
using System.Text;

namespace AiDatabaseAgent.Core.Knowledge;

/// <summary>
/// The lexical half of hybrid retrieval: finds glossary entries whose title or alias appears literally in
/// the question (case-insensitive, whole words, punctuation ignored). Deterministic and explainable —
/// "matched the term 'last month'" — and it catches exactly the cases dense embeddings of short texts miss
/// ("earn" vs "salary", "last month" vs "calendar periods").
/// </summary>
public static class GlossaryMatcher
{
    public sealed record Match(KnowledgeEntry Entry, string Term);

    /// <returns>Matching definition/not-tracked entries, most specific (longest) term first; one match per entry.</returns>
    public static IReadOnlyList<Match> Find(string question, IEnumerable<KnowledgeEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var q = $" {Normalize(question)} ";
        var matches = new List<Match>();
        foreach (var e in entries.Where(e => e.Kind != KnowledgeKind.Example))
        {
            var best = (e.Aliases ?? [])
                .Append(StripParenthetical(e.Title))
                .Select(term => (Term: term, Norm: Normalize(term)))
                .Where(t => t.Norm.Length >= 2 && q.Contains($" {t.Norm} ", StringComparison.Ordinal))
                .OrderByDescending(t => t.Norm.Length)
                .FirstOrDefault();
            if (best.Term is not null)
                matches.Add(new Match(e, best.Term));
        }
        return matches.OrderByDescending(m => Normalize(m.Term).Length).ToList();
    }

    /// <summary>Lower-case, letters and digits only, single spaces.</summary>
    public static string Normalize(string text)
    {
        var sb = new StringBuilder((text ?? "").Length);
        var space = true;
        foreach (var ch in (text ?? "").ToLower(CultureInfo.InvariantCulture))
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
                space = false;
            }
            else if (!space)
            {
                sb.Append(' ');
                space = true;
            }
        }
        return sb.ToString().Trim();
    }

    // "Average order value (AOV)" -> "Average order value": the part in brackets is usually an alias already.
    private static string StripParenthetical(string title)
    {
        var i = title.IndexOf('(', StringComparison.Ordinal);
        return i > 0 ? title[..i].Trim() : title;
    }
}
