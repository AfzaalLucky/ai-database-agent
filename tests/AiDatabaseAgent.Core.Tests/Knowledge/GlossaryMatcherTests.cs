using AiDatabaseAgent.Core.Knowledge;

namespace AiDatabaseAgent.Core.Tests.Knowledge;

public class GlossaryMatcherTests
{
    private static readonly KnowledgeEntry[] Entries =
    [
        new("aov", KnowledgeKind.Definition, "Average order value (AOV)", "…", Aliases: ["AOV", "average basket"]),
        new("periods", KnowledgeKind.Definition, "Calendar periods", "…", Aliases: ["last month", "year to date", "YTD"]),
        new("pay", KnowledgeKind.NotTracked, "Not available: salaries", "…", Aliases: ["salary", "earn"]),
        new("rollup", KnowledgeKind.Definition, "Top-level categories", "…", Aliases: ["main product category", "category"]),
        new("ex", KnowledgeKind.Example, "Example", "…", "SELECT 1", "What is the AOV?"),
    ];

    private static string[] Ids(string question) => GlossaryMatcher.Find(question, Entries).Select(m => m.Entry.Id).ToArray();

    [Theory]
    [InlineData("How many orders did we get last month?", "periods")]
    [InlineData("What's our AOV by segment?", "aov")]
    [InlineData("What is the average order value?", "aov")]
    [InlineData("How much do warehouse staff earn?", "pay")]
    [InlineData("Revenue YTD, please", "periods")]
    public void Finds_titles_and_aliases_case_insensitively(string question, string expected) =>
        Assert.Contains(expected, Ids(question));

    [Theory]
    [InlineData("Which customers learned about us from ads?")] // "earn" inside "learned" is not a match
    [InlineData("Show the monthly trend")] // "month" is not "last month"
    [InlineData("How many orders are there?")]
    public void Matches_whole_words_only(string question) =>
        Assert.Empty(Ids(question));

    [Fact]
    public void Never_matches_examples_and_reports_the_most_specific_term_first()
    {
        var matches = GlossaryMatcher.Find("Revenue by main product category and AOV", Entries);

        Assert.DoesNotContain(matches, m => m.Entry.Kind == KnowledgeKind.Example);
        Assert.Equal("main product category", matches[0].Term);
        Assert.Equal(["rollup", "aov"], matches.Select(m => m.Entry.Id));
    }

    [Theory]
    [InlineData("Year-to-date revenue?", "year to date revenue")]
    [InlineData("  Top-level   (category)!", "top level category")]
    public void Normalizes_punctuation_and_spacing(string input, string expected) =>
        Assert.Equal(expected, GlossaryMatcher.Normalize(input));
}
