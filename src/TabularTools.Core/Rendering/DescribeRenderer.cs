using System.Text;
using TabularTools.Core.Describe;

namespace TabularTools.Core.Rendering;

/// <summary>Markdown and plain-text views of describe results.</summary>
public static class DescribeRenderer
{
    public static string Markdown(DescribeResult result)
    {
        var sb = new StringBuilder();
        var m = result.Model;
        sb.AppendLine($"### Semantic model `{m.Name}`");
        sb.AppendLine();
        sb.AppendLine($"Compatibility level {m.CompatibilityLevel} · {m.Format.ToString().ToUpperInvariant()} · `{m.DefinitionPath}`");
        sb.AppendLine();
        sb.AppendLine("| Tables | Columns | Measures | Relationships | Roles | Calc groups | Partitions | Expressions | Functions |");
        sb.AppendLine("|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        var c = m.Counts;
        sb.AppendLine($"| {c.Tables} | {c.Columns} | {c.Measures} | {c.Relationships} | {c.Roles} | {c.CalculationGroups} | {c.Partitions} | {c.Expressions} | {c.Functions} |");
        sb.AppendLine();

        if (result.Tables is { } tables)
        {
            sb.AppendLine("| Table | Kind | Hidden | Columns | Measures | Partitions |");
            sb.AppendLine("|---|---|:---:|---:|---:|---:|");
            foreach (var t in tables)
                sb.AppendLine($"| {Cell(t.Name)} | {t.Kind} | {(t.IsHidden ? "✓" : "")} | {t.Columns} | {t.Measures} | {t.Partitions} |");
        }

        if (result.Objects is { } objects)
        {
            foreach (var group in objects.GroupBy(o => o.Type))
            {
                sb.AppendLine($"#### {Title(group.Key)} ({group.Count()})");
                sb.AppendLine();
                sb.AppendLine("| Object | Hidden | Type / format | Details |");
                sb.AppendLine("|---|:---:|---|---|");
                foreach (var o in group)
                    sb.AppendLine($"| {Cell(Label(o))} | {(o.IsHidden == true ? "✓" : "")} | {Cell(TypeInfo(o))} | {Cell(Details(o))} |");
                sb.AppendLine();
                if (result.Detail == DetailLevel.Full)
                {
                    foreach (var o in group.Where(o => !string.IsNullOrWhiteSpace(o.Expression)))
                    {
                        sb.AppendLine($"<details><summary>{Escape(Label(o))}</summary>");
                        sb.AppendLine();
                        sb.AppendLine("```" + (o.Type is "partition" or "expression" ? "powerquery" : "dax"));
                        sb.AppendLine(o.Expression!.Trim());
                        sb.AppendLine("```");
                        sb.AppendLine("</details>");
                        sb.AppendLine();
                    }
                }
            }
            if (result.Truncated)
                sb.AppendLine($"_Showing {objects.Count} of {result.TotalObjects} objects. Next cursor: `{result.NextCursor}`._");
        }

        return sb.ToString();
    }

    public static string Text(DescribeResult result)
    {
        var sb = new StringBuilder();
        var m = result.Model;
        var c = m.Counts;
        sb.AppendLine($"{m.Name}  (compatibility level {m.CompatibilityLevel}, {m.Format.ToString().ToUpperInvariant()})");
        sb.AppendLine($"  {m.DefinitionPath}");
        sb.AppendLine($"  {c.Tables} tables, {c.Columns} columns, {c.Measures} measures, {c.Relationships} relationships, {c.Roles} roles, " +
                      $"{c.CalculationGroups} calculation groups, {c.Partitions} partitions, {c.Expressions} expressions, {c.Functions} functions");

        if (result.Tables is { } tables)
        {
            sb.AppendLine();
            var width = tables.Count == 0 ? 5 : Math.Max(5, tables.Max(t => t.Name.Length));
            foreach (var t in tables)
                sb.AppendLine($"  {t.Name.PadRight(width)}  {t.Kind,-16} {t.Columns,3} cols {t.Measures,3} measures{(t.IsHidden ? "  hidden" : "")}");
        }

        if (result.Objects is { } objects)
        {
            foreach (var group in objects.GroupBy(o => o.Type))
            {
                sb.AppendLine();
                sb.AppendLine($"{Title(group.Key)} ({group.Count()})");
                foreach (var o in group)
                {
                    var extra = string.Join("  ", new[] { TypeInfo(o), Details(o), o.IsHidden == true ? "hidden" : "" }.Where(x => x.Length > 0));
                    sb.AppendLine($"  {Label(o)}{(extra.Length > 0 ? "  —  " + extra : "")}");
                    if (result.Detail == DetailLevel.Full && !string.IsNullOrWhiteSpace(o.Expression))
                        foreach (var line in o.Expression.Trim().Split('\n'))
                            sb.AppendLine("      " + line.TrimEnd('\r'));
                }
            }
            if (result.Truncated)
            {
                sb.AppendLine();
                sb.AppendLine($"Showing {objects.Count} of {result.TotalObjects} objects. Next cursor: {result.NextCursor}");
            }
        }

        return sb.ToString();
    }

    private static string Label(DescribedObject o) => o.Type switch
    {
        "column" => $"{ModelDescriber.Quote(o.Table!)}[{o.Name}]",
        "measure" => $"{ModelDescriber.Quote(o.Table!)}[{o.Name}]",
        "relationship" => $"{Prop(o, "from")} → {Prop(o, "to")}",
        "table" => ModelDescriber.Quote(o.Name),
        null => o.Name,
        _ => o.Table is null ? o.Name : $"{ModelDescriber.Quote(o.Table)} / {o.Name}",
    };

    private static string TypeInfo(DescribedObject o) => o.Type switch
    {
        "column" => Join(o.DataType, o.FormatString, o.SummarizeBy is { } s && s != "none" ? $"sum by {s}" : null),
        "measure" => o.FormatString ?? "",
        "table" => Prop(o, "kind"),
        "partition" => Join(Prop(o, "sourceType"), Prop(o, "mode")),
        "relationship" => Join(Prop(o, "cardinality"), Prop(o, "crossFilteringBehavior")),
        "role" => Prop(o, "modelPermission"),
        "expression" => Join(Prop(o, "kind"), Prop(o, "isParameter") == "True" ? "parameter" : null),
        _ => "",
    };

    private static string Details(DescribedObject o) => o.Type switch
    {
        "column" => Join(Prop(o, "kind") is "data" or "" ? null : Prop(o, "kind"), Prop(o, "isKey") == "True" ? "key" : null,
            Prop(o, "sortByColumn") is { Length: > 0 } sb ? $"sort by {sb}" : null, o.DisplayFolder),
        "measure" => o.DisplayFolder ?? "",
        "relationship" => Prop(o, "isActive") == "False" ? "inactive" : "",
        "hierarchy" => Prop(o, "levels"),
        "calculationItem" => $"ordinal {Prop(o, "ordinal")}",
        "role" => $"{Prop(o, "members")} members; tables: {Prop(o, "tablePermissions")}",
        _ => "",
    };

    private static string Prop(DescribedObject o, string name)
    {
        if (o.Properties is null || !o.Properties.TryGetValue(name, out var value) || value is null) return "";
        return value switch
        {
            IEnumerable<string> list => string.Join(", ", list),
            System.Collections.IEnumerable list and not string => string.Join(", ", list.Cast<object>().Select(x =>
                x is IDictionary<string, object?> d && d.TryGetValue("table", out var t) ? t?.ToString() : x?.ToString())),
            _ => value.ToString() ?? "",
        };
    }

    private static string Join(params string?[] parts) => string.Join(", ", parts.Where(p => !string.IsNullOrEmpty(p)));

    private static string Title(string type) => type switch
    {
        "calculationItem" => "Calculation items",
        "hierarchy" => "Hierarchies",
        _ => char.ToUpperInvariant(type[0]) + type[1..] + "s",
    };

    private static string Cell(string value) => Escape(value).Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

    private static string Escape(string value) => value.Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);
}
