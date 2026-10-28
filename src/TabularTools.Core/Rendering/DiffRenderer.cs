using System.Text;
using TabularTools.Core.Dax;
using TabularTools.Core.Describe;
using TabularTools.Core.Diff;

namespace TabularTools.Core.Rendering;

/// <summary>Text and Markdown (PR comment, ARCHITECTURE §10.7) views of semantic diffs.</summary>
public static class DiffRenderer
{
    public const string DefaultMarker = "diff";

    public static string SummaryLine(DiffResult r)
    {
        var verdict = r.Blocking ? $"blocking (fail on {Name(r.FailOn)})" : "not blocking";
        if (r.Models.Count == 0) return $"No semantic models changed between {r.Base.Label} and {r.Head.Label}.";
        var models = string.Join("; ", r.Models.Select(m => $"'{m.Name}' {Counts(m.Summary, plain: true)}"));
        return $"{r.Base.Label} → {r.Head.Label}: {models} — {verdict}.";
    }

    public static string Text(DiffResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine(SummaryLine(r));
        foreach (var model in r.Models)
        {
            sb.AppendLine();
            sb.AppendLine($"{model.ModelPath} ({StatusText(model.Status)})");
            foreach (var c in model.Changes)
            {
                sb.Append("  ").Append(Name(c.Severity).ToUpperInvariant().PadRight(9)).Append(Label(c)).Append("  ").Append(ChangeText(c, markdown: false));
                if (c.Impact is { } impact) sb.Append($"  [{impact.DownstreamCount} downstream]");
                if (c.Location is { } loc) sb.Append($"  ({loc.File}:{loc.Line})");
                sb.AppendLine();
            }
            if (model.Summary.FormattingOnlyHidden > 0)
                sb.AppendLine($"  {model.Summary.FormattingOnlyHidden} formatting-only expression change(s) hidden.");
        }
        AppendDiagnostics(sb, r, markdown: false);
        return sb.ToString();
    }

    /// <summary>The whole diff as one PR comment.</summary>
    public static string Markdown(DiffResult r, string? marker = DefaultMarker)
    {
        var sb = new StringBuilder();
        if (marker is not null) sb.AppendLine(Marker(marker));
        if (r.Models.Count == 0)
        {
            sb.AppendLine($"### Semantic model diff · base `{r.Base.Label}` → head `{r.Head.Label}`");
            sb.AppendLine();
            sb.AppendLine("No semantic model changes.");
        }
        foreach (var model in r.Models) sb.Append(ModelMarkdown(model, r.Base, r.Head));
        AppendDiagnostics(sb, r, markdown: true);
        sb.AppendLine();
        sb.AppendLine(Footer());
        return sb.ToString();
    }

    public static string Marker(string marker) => $"<!-- tabulartools:{marker} -->";

    public static string Footer() => $"<sub>{ProductInfo.Name} {ProductInfo.Version}</sub>";

    /// <summary>One model's section: heading, counts, a table per severity, and expression diffs.</summary>
    public static string ModelMarkdown(ModelDiff m, DiffSide @base, DiffSide head, int headingLevel = 3)
    {
        var h = new string('#', headingLevel);
        var sb = new StringBuilder();
        sb.AppendLine($"{h} Semantic model review — `{m.Name}` · base `{@base.Label}` → head `{head.Label}`");
        sb.AppendLine();
        var status = m.Status switch
        {
            ModelDiffStatus.Added => " — new model",
            ModelDiffStatus.Removed => " — model removed",
            _ => "",
        };
        sb.AppendLine(m.Changes.Count == 0 && m.Status == ModelDiffStatus.Unchanged
            ? "✅ No semantic changes." + (m.Summary.FormattingOnlyHidden > 0 ? $" ({FormattingNote(m.Summary.FormattingOnlyHidden)})" : "")
            : Counts(m.Summary, plain: false) + status);
        sb.AppendLine();

        foreach (var group in m.Changes.GroupBy(c => c.Severity).OrderByDescending(g => g.Key))
        {
            var count = group.Count();
            var open = group.Key == ChangeSeverity.Breaking;
            sb.AppendLine(open ? $"{h}# {Icon(group.Key)} {Title(group.Key)} ({count})" : $"<details><summary>{Icon(group.Key)} {Title(group.Key)} ({count})</summary>");
            sb.AppendLine();
            sb.AppendLine("| Object | Change | Impact |");
            sb.AppendLine("|---|---|---|");
            foreach (var c in group) sb.AppendLine($"| {ObjectCell(c)} | {ChangeText(c, markdown: true)} | {ImpactCell(c)} |");
            sb.AppendLine();
            if (!open) { sb.AppendLine("</details>"); sb.AppendLine(); }
        }

        var expressionChanges = m.Changes.Where(c => !string.IsNullOrEmpty(c.Details?.ExpressionDiff)).ToList();
        if (expressionChanges.Count > 0)
        {
            sb.AppendLine($"<details><summary>Expression diffs ({expressionChanges.Count})</summary>");
            sb.AppendLine();
            foreach (var c in expressionChanges)
            {
                sb.AppendLine($"**{Escape(Label(c))}** — {Escape(KindPhrase(c.Kind))}{(c.Location is { } loc ? $" · `{loc.File}:{loc.Line}`" : "")}");
                sb.AppendLine();
                sb.AppendLine("```diff");
                sb.AppendLine(c.Details!.ExpressionDiff!.Replace("```", "`​``", StringComparison.Ordinal));
                sb.AppendLine("```");
                sb.AppendLine();
            }
            sb.AppendLine("</details>");
            sb.AppendLine();
        }
        if (m.Summary.FormattingOnlyHidden > 0 && m.Changes.Count > 0)
        {
            sb.AppendLine($"_{FormattingNote(m.Summary.FormattingOnlyHidden)}._");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string FormattingNote(int n) => $"{n} formatting-only expression change{(n == 1 ? "" : "s")} hidden";

    public static string Counts(DiffSummary s, bool plain)
    {
        var parts = new List<string>();
        void Add(int n, ChangeSeverity sev)
        {
            if (n > 0 || !plain) parts.Add(plain ? $"{n} {Name(sev)}" : $"{Icon(sev)} {n} {Name(sev)}");
        }
        Add(s.Breaking, ChangeSeverity.Breaking);
        Add(s.High, ChangeSeverity.High);
        Add(s.Medium, ChangeSeverity.Medium);
        Add(s.Low, ChangeSeverity.Low);
        Add(s.Info, ChangeSeverity.Info);
        if (plain && parts.Count == 0) return "no changes";
        return string.Join(plain ? ", " : " · ", parts);
    }

    // ---- Cells and phrases ----

    private static string ObjectCell(Change c)
    {
        if (c.Rename is { } r)
        {
            var before = c.Object.Type switch
            {
                "measure" or "column" => Label(c.Object with { Name = r.From, Table = c.Kind == "measure.renamed" ? c.Object.Table : c.Object.Table }),
                _ => Label(c.Object with { Name = r.From }),
            };
            return $"{Code(before)} → {Code(Label(c))}";
        }
        if (c.Kind == "measure.moved") return $"{Code(Label(c.Object with { Table = c.Before }))} → {Code(Label(c))}";
        return Code(Label(c));
    }

    private static string ImpactCell(Change c)
    {
        if (c.Object.Type is "role" or "tablePermission") return "security";
        if (c.Impact is not { } impact) return "—";
        var names = impact.Downstream.Take(3).Select(k => Code(KeyLabel(k)));
        var more = impact.DownstreamCount > 3 ? $" +{impact.DownstreamCount - 3} more" : "";
        return $"{impact.DownstreamCount} downstream: {string.Join(", ", names)}{more}";
    }

    /// <summary>What happened, in a few words.</summary>
    public static string ChangeText(Change c, bool markdown)
    {
        string V(string? value) => markdown ? Code(OneLine(value)) : $"'{OneLine(value)}'";
        var type = TypeName(c.Object.Type);
        var note = c.Details?.Note is { } n ? $" ({n})" : "";
        return c.Kind switch
        {
            _ when c.Rename is { } r => $"{type} renamed ({r.Confidence}: {EvidenceText(r.Evidence)})",
            "measure.moved" => $"measure moved from table {V(c.Before)} to {V(c.After)}",
            _ when c.Kind.EndsWith(".added", StringComparison.Ordinal) => $"{type} added{note}",
            _ when c.Kind.EndsWith(".removed", StringComparison.Ordinal) => $"{type} removed{note}",
            _ when c.Kind.EndsWith(".hidden", StringComparison.Ordinal) => $"{type} hidden",
            _ when c.Kind.EndsWith(".unhidden", StringComparison.Ordinal) => $"{type} unhidden",
            "role.rlsFilterAdded" => $"RLS filter added {V(c.After)}",
            "role.rlsFilterRemoved" => $"RLS filter removed (was {V(c.Before)})",
            _ when c.Details?.ExpressionDiff is not null && c.Details.FormattingOnly == true => $"{KindPhrase(c.Kind)}",
            _ when c.Details?.ExpressionDiff is not null && IsShort(c.Before) && IsShort(c.After) => $"{KindPhrase(c.Kind)} {V(c.Before)} → {V(c.After)}",
            _ when c.Details?.ExpressionDiff is not null => $"{KindPhrase(c.Kind)} (see diff)",
            _ => $"{KindPhrase(c.Kind)} {V(c.Before)} → {V(c.After)}{note}",
        };
    }

    private static bool IsShort(string? value) => value is null || value.Length <= 60 && !value.Contains('\n', StringComparison.Ordinal);

    /// <summary>Human phrase for a change kind: <c>column.summarizeByChanged</c> → "summarize by changed".</summary>
    public static string KindPhrase(string kind) => kind switch
    {
        "role.rlsFilterChanged" => "RLS filter changed",
        "role.rlsFilterFormatting" => "RLS filter reformatted",
        "role.olsChanged" => "object-level security changed",
        "relationship.crossFilterChanged" => "cross-filter direction changed",
        "relationship.referentialIntegrityChanged" => "assume referential integrity changed",
        "column.keyDataTypeChanged" => "data type changed on a relationship key",
        "partition.sourceChanged" => "source changed",
        "partition.sourceFormatting" => "source reformatted",
        "measure.expressionChanged" or "column.expressionChanged" or "table.expressionChanged" or "calculationItem.expressionChanged" or "function.expressionChanged" => "DAX changed",
        "expression.expressionChanged" => "expression changed",
        _ when kind.EndsWith("Formatting", StringComparison.Ordinal) => "reformatted (whitespace or comments only)",
        _ => Words(kind[(kind.IndexOf('.', StringComparison.Ordinal) + 1)..]),
    };

    private static string Words(string camel)
    {
        var sb = new StringBuilder();
        foreach (var ch in camel)
        {
            if (char.IsUpper(ch)) sb.Append(' ').Append(char.ToLowerInvariant(ch));
            else sb.Append(ch);
        }
        return sb.ToString().Replace("is key", "key flag", StringComparison.Ordinal);
    }

    private static string EvidenceText(string evidence) => evidence switch
    {
        "lineageTag" => "lineage tag",
        "sourceColumn" => "same source column",
        "expression" => "similar expression",
        "columns" => "similar columns",
        "levels" => "same levels",
        "name" => "case change",
        _ => evidence,
    };

    private static string TypeName(string type) => type switch
    {
        "calculationGroup" => "calculation group",
        "calculationItem" => "calculation item",
        "tablePermission" => "table permission",
        "function" => "DAX function",
        _ => type,
    };

    public static string Label(Change c) => Label(c.Object);

    /// <summary>Readable object name: <c>Sales[Amount]</c>, <c>'Date'</c>, <c>Sales[DateKey] → Date[DateKey]</c>, <c>role West Region · Store</c>.</summary>
    public static string Label(ObjectRef o) => o.Type switch
    {
        "column" or "measure" => $"{ModelDescriber.Quote(o.Table ?? "?")}[{o.Name}]",
        "table" or "calculationGroup" => ModelDescriber.Quote(o.Name),
        "model" => "(model)",
        "relationship" => o.Name.Replace("->", " → ", StringComparison.Ordinal),
        "tablePermission" => $"role {RoleOf(o.Key)} · {o.Name}",
        "role" => $"role {o.Name}",
        "partition" or "hierarchy" or "calculationItem" => $"{ModelDescriber.Quote(o.Table ?? "?")} {TypeName(o.Type)} {o.Name}",
        _ => $"{TypeName(o.Type)} {o.Name}",
    };

    /// <summary>Readable name for an object key (used for impact lists).</summary>
    public static string KeyLabel(string key)
    {
        var last = key.Split('/')[^1];
        var type = last.Contains(':', StringComparison.Ordinal) ? last[..last.IndexOf(':', StringComparison.Ordinal)] : "";
        var (table, name) = DependencyQuery.SplitKey(key);
        return Label(new ObjectRef(key, type, name, table));
    }

    private static string RoleOf(string key)
    {
        var first = key.Split('/')[0];
        return first.StartsWith("role:", StringComparison.Ordinal) ? first["role:".Length..] : first;
    }

    private static void AppendDiagnostics(StringBuilder sb, DiffResult r, bool markdown)
    {
        if (r.Diagnostics is not { Count: > 0 } diagnostics) return;
        sb.AppendLine();
        foreach (var d in diagnostics) sb.AppendLine(markdown ? $"> ⚠️ {Escape(d.Message)}" : $"{d.Level.ToString().ToLowerInvariant()} {d.Code}: {d.Message}");
    }

    private static string StatusText(ModelDiffStatus s) => s switch
    {
        ModelDiffStatus.Added => "new model",
        ModelDiffStatus.Removed => "removed",
        ModelDiffStatus.Unchanged => "no semantic changes",
        _ => "modified",
    };

    public static string Icon(ChangeSeverity s) => s switch
    {
        ChangeSeverity.Breaking => "🔴",
        ChangeSeverity.High => "🟠",
        ChangeSeverity.Medium => "🟡",
        ChangeSeverity.Low => "⚪",
        _ => "▫️",
    };

    private static string Title(ChangeSeverity s) => s switch
    {
        ChangeSeverity.Breaking => "Breaking",
        ChangeSeverity.High => "High",
        ChangeSeverity.Medium => "Medium",
        ChangeSeverity.Low => "Low",
        _ => "Info",
    };

    public static string Name(ChangeSeverity s) => s.ToString().ToLowerInvariant();

    public static string Name(DiffFailOn f) => f.ToString().ToLowerInvariant();

    private static string OneLine(string? value)
    {
        if (value is null) return "";
        var single = string.Join(' ', value.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()));
        return single.Length > 80 ? single[..77] + "..." : single;
    }

    /// <summary>A Markdown code span that survives table cells: pipes escaped, backticks handled.</summary>
    internal static string Code(string value)
    {
        var text = value.Replace("|", "\\|", StringComparison.Ordinal);
        return text.Contains('`', StringComparison.Ordinal) ? $"`` {text} ``" : $"`{text}`";
    }

    internal static string Escape(string value) =>
        value.Replace("|", "\\|", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}
