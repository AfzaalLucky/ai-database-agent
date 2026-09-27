using System.Globalization;

namespace AiDatabaseAgent.Core.Evaluation;

public sealed record ComparisonResult(bool IsMatch, string Reason)
{
    public static readonly ComparisonResult Match = new(true, "match");
}

/// <summary>
/// Execution-accuracy comparison: does the agent's result contain the gold result?
/// <list type="bullet">
///   <item>every gold column must correspond to a distinct agent column (names are ignored; extra agent columns are fine);</item>
///   <item>row counts must be equal; rows are compared as a multiset, or in sequence when <c>ordered</c>;</item>
///   <item>numbers are equal within 0.006 absolute (so 2-decimal rounding passes) or 1e-6 relative;
///         with <c>ignoreSign</c> their absolute values are compared;</item>
///   <item>strings compare case-insensitively after trimming; dates compare by value; numeric strings compare as numbers.</item>
/// </list>
/// </summary>
public static class ResultComparer
{
    public static ComparisonResult Compare(
        IReadOnlyList<object?[]> gold,
        IReadOnlyList<object?[]> actual,
        bool ordered = false,
        bool ignoreSign = false)
    {
        ArgumentNullException.ThrowIfNull(gold);
        ArgumentNullException.ThrowIfNull(actual);
        if (gold.Count != actual.Count)
            return new(false, $"expected {gold.Count} row(s), got {actual.Count}");
        if (gold.Count == 0)
            return ComparisonResult.Match;

        var goldCols = gold[0].Length;
        var actualCols = actual[0].Length;
        if (actualCols < goldCols)
            return new(false, $"expected at least {goldCols} column(s), got {actualCols}");

        var g = gold.Select(r => r.Select(Normalize).ToArray()).ToList();
        var a = actual.Select(r => r.Select(Normalize).ToArray()).ToList();

        // Candidate agent columns for each gold column: same values (as a multiset, or in sequence).
        var candidates = new List<int>[goldCols];
        for (var gc = 0; gc < goldCols; gc++)
        {
            candidates[gc] = [];
            for (var ac = 0; ac < actualCols; ac++)
                if (ColumnMatches(g, gc, a, ac, ordered, ignoreSign))
                    candidates[gc].Add(ac);
            if (candidates[gc].Count == 0)
                return new(false, $"no column matches gold column {gc + 1} (e.g. {Describe(g[0][gc])})");
        }

        var mapping = new int[goldCols];
        var used = new bool[actualCols];
        return Assign(0) ? ComparisonResult.Match : new(false, "column values match individually but not row by row");

        bool Assign(int gc)
        {
            if (gc == goldCols)
                return RowsMatch(g, a, mapping, ordered, ignoreSign);
            foreach (var ac in candidates[gc])
            {
                if (used[ac])
                    continue;
                used[ac] = true;
                mapping[gc] = ac;
                if (Assign(gc + 1))
                    return true;
                used[ac] = false;
            }
            return false;
        }
    }

    private static bool ColumnMatches(List<Cell[]> g, int gc, List<Cell[]> a, int ac, bool ordered, bool ignoreSign)
    {
        if (ordered)
        {
            for (var r = 0; r < g.Count; r++)
                if (!Equal(g[r][gc], a[r][ac], ignoreSign))
                    return false;
            return true;
        }
        var gv = g.Select(r => r[gc]).OrderBy(c => c, CellOrder.Instance).ToList();
        var av = a.Select(r => r[ac]).OrderBy(c => c, CellOrder.Instance).ToList();
        if (ignoreSign)
        {
            gv = gv.Select(Abs).OrderBy(c => c, CellOrder.Instance).ToList();
            av = av.Select(Abs).OrderBy(c => c, CellOrder.Instance).ToList();
        }
        return gv.Zip(av).All(p => Equal(p.First, p.Second, ignoreSign));
    }

    private static bool RowsMatch(List<Cell[]> g, List<Cell[]> a, int[] mapping, bool ordered, bool ignoreSign)
    {
        bool Row(Cell[] gr, Cell[] ar) => mapping.Select((ac, gc) => Equal(gr[gc], ar[ac], ignoreSign)).All(x => x);

        if (ordered)
            return g.Zip(a).All(p => Row(p.First, p.Second));

        var remaining = new List<Cell[]>(a);
        foreach (var gr in g)
        {
            var i = remaining.FindIndex(ar => Row(gr, ar));
            if (i < 0)
                return false;
            remaining.RemoveAt(i);
        }
        return true;
    }

    /* ------------------------------------------------------------ cells */

    private enum CellKind
    {
        Null,
        Number,
        Text,
    }

    private readonly record struct Cell(CellKind Kind, double Number, string Text);

    private static Cell Normalize(object? value) => value switch
    {
        null or DBNull => new Cell(CellKind.Null, 0, ""),
        bool b => new Cell(CellKind.Number, b ? 1 : 0, ""),
        byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal =>
            new Cell(CellKind.Number, Convert.ToDouble(value, CultureInfo.InvariantCulture), ""),
        DateTime d => new Cell(CellKind.Text, 0, d.TimeOfDay == TimeSpan.Zero ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : d.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)),
        DateTimeOffset o => Normalize(o.DateTime),
        DateOnly d => new Cell(CellKind.Text, 0, d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
        string s when double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) => new Cell(CellKind.Number, n, ""),
        string s when DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) && s.Length >= 10 && s[4] == '-' => Normalize(d),
        _ => new Cell(CellKind.Text, 0, Convert.ToString(value, CultureInfo.InvariantCulture)!.Trim()),
    };

    private static Cell Abs(Cell c) => c.Kind == CellKind.Number ? c with { Number = Math.Abs(c.Number) } : c;

    private static bool Equal(Cell x, Cell y, bool ignoreSign)
    {
        if (x.Kind != y.Kind)
            return false;
        return x.Kind switch
        {
            CellKind.Null => true,
            CellKind.Number => NumbersEqual(ignoreSign ? Math.Abs(x.Number) : x.Number, ignoreSign ? Math.Abs(y.Number) : y.Number),
            _ => string.Equals(x.Text, y.Text, StringComparison.OrdinalIgnoreCase),
        };
    }

    private static bool NumbersEqual(double x, double y) =>
        Math.Abs(x - y) <= Math.Max(0.006, 1e-6 * Math.Max(Math.Abs(x), Math.Abs(y)));

    private static string Describe(Cell c) => c.Kind switch
    {
        CellKind.Null => "NULL",
        CellKind.Number => c.Number.ToString("G10", CultureInfo.InvariantCulture),
        _ => $"'{c.Text}'",
    };

    private sealed class CellOrder : IComparer<Cell>
    {
        public static readonly CellOrder Instance = new();

        public int Compare(Cell x, Cell y) =>
            x.Kind != y.Kind ? x.Kind.CompareTo(y.Kind)
            : x.Kind == CellKind.Number ? x.Number.CompareTo(y.Number)
            : string.Compare(x.Text, y.Text, StringComparison.OrdinalIgnoreCase);
    }
}
