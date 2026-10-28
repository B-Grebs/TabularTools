using System.Text;
using TabularTools.Core.Snapshots;

namespace TabularTools.Core.Testing;

/// <param name="Label">Column label: the head measure name, or <c>Table[Column]</c> for group-by columns.</param>
/// <param name="Key">Object key of the head measure or column.</param>
public sealed record PlannedColumn(string Label, string Key, bool IsGroup);

/// <summary>A query for one case and side. Results are matched to <see cref="Columns"/> by position.</summary>
public sealed record PlannedQuery(string Text, IReadOnlyList<PlannedColumn> Columns, string? Role, string Label);

/// <summary>Writes the DAX for test cases (ARCHITECTURE §11.3). Output is deterministic, so recordings match it.</summary>
public static class QueryPlanner
{
    /// <summary>
    /// The case's measures by its group-by columns, with <paramref name="defs"/> defined in the query.
    /// <paramref name="names"/> maps head measure keys to the name to use on this side; measures it doesn't map are left out.
    /// </summary>
    public static PlannedQuery Values(ResolvedCase c, SideDefinitions defs, Func<MeasureSnapshot, string?> names, string label)
    {
        ArgumentNullException.ThrowIfNull(c);
        var columns = c.GroupBy.Select(g => new PlannedColumn($"{g.Table}[{g.Name}]", g.Key, true)).ToList();
        var exprs = new List<string>();
        foreach (var m in c.Measures)
        {
            if (names(m) is not { } name) continue;
            columns.Add(new PlannedColumn(m.Name, m.Key, false));
            exprs.Add($"{Label(m.Name)}, {Ref(name)}");
        }
        return new PlannedQuery(Build(c, defs, exprs), columns, c.Case.AsRole, label);
    }

    /// <summary>The rows (group-by values) where the invariant is false, among rows where any case measure has a value.</summary>
    public static PlannedQuery Invariant(ResolvedCase c, string expr, SideDefinitions defs, string label)
    {
        ArgumentNullException.ThrowIfNull(c);
        var anyValue = string.Join(" && ", c.Measures.Select(m => $"ISBLANK ( {Ref(m.Name)} )"));
        var violation = $"{Label(ViolationColumn)}, IF ( NOT ( {anyValue} ) && NOT ( {expr.Trim()} ), 1 )";
        var columns = c.GroupBy.Select(g => new PlannedColumn($"{g.Table}[{g.Name}]", g.Key, true))
            .Append(new PlannedColumn(ViolationColumn, ViolationColumn, false)).ToList();
        return new PlannedQuery(Build(c, defs, [violation]), columns, c.Case.AsRole, label);
    }

    public const string ViolationColumn = "__violation";

    private static string Build(ResolvedCase c, SideDefinitions defs, IReadOnlyList<string> expressions)
    {
        var sb = new StringBuilder();
        if (!defs.IsEmpty)
        {
            sb.Append("DEFINE\n");
            foreach (var d in defs.Columns) Define(sb, "COLUMN", d);
            foreach (var d in defs.Measures) Define(sb, "MEASURE", d);
        }
        sb.Append("EVALUATE\n");
        var hasFilters = c.Filters.Count > 0;
        var indent = hasFilters ? "\t" : "";
        if (hasFilters) sb.Append("CALCULATETABLE (\n");
        sb.Append(indent).Append("SUMMARIZECOLUMNS (\n");
        var args = c.GroupBy.Select(g => Column(g.Table, g.Name)).Concat(expressions).ToList();
        for (var i = 0; i < args.Count; i++)
            sb.Append(indent).Append('\t').Append(args[i]).Append(i < args.Count - 1 ? ",\n" : "\n");
        sb.Append(indent).Append(')');
        if (hasFilters)
        {
            foreach (var f in c.Filters) sb.Append(",\n\t").Append(f);
            sb.Append("\n)");
        }
        sb.Append('\n');
        if (c.GroupBy.Count > 0) sb.Append("ORDER BY ").Append(string.Join(", ", c.GroupBy.Select(g => Column(g.Table, g.Name)))).Append('\n');
        return sb.ToString();
    }

    private static void Define(StringBuilder sb, string kind, QueryDefinition d)
    {
        sb.Append('\t').Append(kind).Append(' ').Append(Column(d.Table, d.Name)).Append(" =\n");
        var lines = d.Expression.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        // A trailing line comment would swallow nothing (a newline follows), but keep the body on its own lines anyway.
        foreach (var line in lines.Select(l => l.TrimEnd()).SkipWhile(l => l.Length == 0).Reverse().SkipWhile(l => l.Length == 0).Reverse())
            sb.Append("\t\t").Append(line).Append('\n');
    }

    public static string Table(string name) => "'" + name.Replace("'", "''", StringComparison.Ordinal) + "'";

    public static string Column(string table, string name) => Table(table) + Ref(name);

    public static string Ref(string name) => "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]";

    public static string Label(string text) => "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
