using AiDatabaseAgent.Core.Evaluation;

namespace AiDatabaseAgent.Core.Tests.Evaluation;

public class ResultComparerTests
{
    private static object?[][] Rows(params object?[][] rows) => rows;

    private static bool Match(object?[][] gold, object?[][] actual, bool ordered = false, bool ignoreSign = false) =>
        ResultComparer.Compare(gold, actual, ordered, ignoreSign).IsMatch;

    [Fact]
    public void Extra_columns_and_column_order_do_not_matter()
    {
        var gold = Rows(["Canada", 43], ["France", 44]);
        var actual = Rows([44, "France", "FR"], [43, "Canada", "CA"]);

        Assert.True(Match(gold, actual));
    }

    [Fact]
    public void Row_order_matters_only_when_ordered()
    {
        var gold = Rows([160], [128], [165]);
        var shuffled = Rows([128], [165], [160]);

        Assert.True(Match(gold, shuffled));
        Assert.False(Match(gold, shuffled, ordered: true));
        Assert.True(Match(gold, Rows(["2025-01", 160], ["2025-02", 128], ["2025-03", 165]), ordered: true));
    }

    [Fact]
    public void Numbers_tolerate_rounding_to_two_decimals_but_not_real_differences()
    {
        Assert.True(Match(Rows([5.320945945945m]), Rows([5.32m])));
        Assert.True(Match(Rows([7138616.62m]), Rows([7138616.62d])));
        Assert.False(Match(Rows([5.32m]), Rows([0.0532m])));
        Assert.False(Match(Rows([475.28m]), Rows([236.62m])));
    }

    [Fact]
    public void Sign_can_be_ignored_per_question()
    {
        Assert.False(Match(Rows([-241322.72m]), Rows([241322.72m])));
        Assert.True(Match(Rows([-241322.72m]), Rows([241322.72m]), ignoreSign: true));
    }

    [Fact]
    public void Strings_are_case_insensitive_and_numeric_strings_compare_as_numbers()
    {
        Assert.True(Match(Rows(["Midland Hardware Supply"]), Rows(["midland hardware supply "])));
        Assert.True(Match(Rows([600]), Rows(["600"])));
    }

    [Fact]
    public void Dates_compare_by_value()
    {
        Assert.True(Match(Rows([new DateTime(2026, 1, 1)]), Rows(["2026-01-01T00:00:00"])));
        Assert.False(Match(Rows([new DateTime(2026, 1, 1)]), Rows([new DateTime(2026, 2, 1)])));
    }

    [Fact]
    public void Row_counts_must_match()
    {
        var r = ResultComparer.Compare(Rows([1], [2]), Rows([1]));

        Assert.False(r.IsMatch);
        Assert.Equal("expected 2 row(s), got 1", r.Reason);
    }

    [Fact]
    public void Columns_must_line_up_row_by_row_not_just_as_sets()
    {
        // Each column has the right values, but pairs are swapped: Canada/44, France/43.
        var gold = Rows(["Canada", 43], ["France", 44]);
        var actual = Rows(["Canada", 44], ["France", 43]);

        Assert.False(Match(gold, actual));
    }

    [Fact]
    public void Duplicate_values_find_a_consistent_mapping()
    {
        // Two numeric columns share values; the mapping must pick the one that lines up.
        var gold = Rows(["A", 1], ["B", 2]);
        var actual = Rows([2, "A", 1], [1, "B", 2]);

        Assert.True(Match(gold, actual));
    }

    [Fact]
    public void Nulls_match_nulls()
    {
        Assert.True(Match(Rows([null, 1]), Rows([1, null])));
        Assert.False(Match(Rows(new object?[] { null }), Rows([0])));
    }
}
