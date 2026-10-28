using System.Text;

namespace TabularTools.Core.Diff;

/// <summary>Line diffs inside one expression, and token similarity for rename heuristics.</summary>
public static class TextDiff
{
    /// <summary>Unified diff (<c>@@ -a,b +c,d @@</c> hunks with 3 lines of context) between two texts. Empty when equal.</summary>
    public static string Unified(string? before, string? after, int context = 3)
    {
        var a = SplitLines(before);
        var b = SplitLines(after);
        var ops = Lcs(a, b);
        if (ops.All(o => o.Op == ' ')) return "";

        var sb = new StringBuilder();
        var i = 0;
        while (i < ops.Count)
        {
            if (ops[i].Op == ' ') { i++; continue; }
            // Hunk: from first change minus context to last change (with gaps ≤ 2*context) plus context.
            var start = Math.Max(0, i - context);
            var end = i;
            while (true)
            {
                var next = end + 1;
                while (next < ops.Count && ops[next].Op == ' ') next++;
                if (next < ops.Count && next - end - 1 <= 2 * context) { end = next; continue; }
                break;
            }
            var stop = Math.Min(ops.Count, end + context + 1);

            int aStart = ops[start].A, bStart = ops[start].B;
            var aCount = ops.Skip(start).Take(stop - start).Count(o => o.Op != '+');
            var bCount = ops.Skip(start).Take(stop - start).Count(o => o.Op != '-');
            sb.Append("@@ -").Append(Range(aStart, aCount)).Append(" +").Append(Range(bStart, bCount)).Append(" @@\n");
            for (var k = start; k < stop; k++) sb.Append(ops[k].Op).Append(ops[k].Text).Append('\n');
            i = stop;
        }
        return sb.ToString().TrimEnd('\n');
    }

    private static string Range(int start, int count) => count == 1 ? $"{start + 1}" : $"{(count == 0 ? start : start + 1)},{count}";

    private readonly record struct DiffOp(char Op, string Text, int A, int B);

    private static List<DiffOp> Lcs(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var n = a.Count;
        var m = b.Count;
        var table = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
            for (var j = m - 1; j >= 0; j--)
                table[i, j] = a[i] == b[j] ? table[i + 1, j + 1] + 1 : Math.Max(table[i + 1, j], table[i, j + 1]);

        var ops = new List<DiffOp>();
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (a[x] == b[y]) { ops.Add(new(' ', a[x], x, y)); x++; y++; }
            else if (table[x + 1, y] >= table[x, y + 1]) { ops.Add(new('-', a[x], x, y)); x++; }
            else { ops.Add(new('+', b[y], x, y)); y++; }
        }
        while (x < n) { ops.Add(new('-', a[x], x, y)); x++; }
        while (y < m) { ops.Add(new('+', b[y], x, y)); y++; }
        return ops;
    }

    private static List<string> SplitLines(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        return text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n').ToList();
    }

    /// <summary>1 − Levenshtein distance / longer length, over token sequences. 1 means identical.</summary>
    public static double TokenSimilarity(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count == 0 && b.Count == 0) return 1;
        if (a.Count == 0 || b.Count == 0) return 0;
        var previous = new int[b.Count + 1];
        var current = new int[b.Count + 1];
        for (var j = 0; j <= b.Count; j++) previous[j] = j;
        for (var i = 1; i <= a.Count; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Count; j++)
            {
                var cost = string.Equals(a[i - 1], b[j - 1], StringComparison.Ordinal) ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }
        return 1.0 - (double)previous[b.Count] / Math.Max(a.Count, b.Count);
    }
}
