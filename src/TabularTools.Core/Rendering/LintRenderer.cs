using System.Text;
using TabularTools.Core.Dax;
using TabularTools.Core.Lint;

namespace TabularTools.Core.Rendering;

/// <summary>Markdown (PR comments) and plain-text views of lint results.</summary>
public static class LintRenderer
{
    public static string SummaryLine(LintResult r)
    {
        var s = r.Summary;
        var verdict = r.Blocking ? $"blocking (fail on {r.FailOn.ToString().ToLowerInvariant()})" : "not blocking";
        var since = s.New is { } n ? $" ({n} new since {r.BaseRef}, {s.Existing} existing)" : "";
        return $"'{r.Model.Name}': {Count(s.Error, "error")}, {Count(s.Warning, "warning")}, {Count(s.Info, "info")}{since} — {verdict}. " +
               $"{Count(s.RulesEvaluated, "rule")} evaluated, {s.RulesSkipped} skipped, {s.Suppressed} suppressed.";
    }

    public static string Text(LintResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine(SummaryLine(r));
        if (r.Violations.Count > 0)
        {
            sb.AppendLine();
            var idWidth = Math.Min(50, r.Violations.Max(v => v.RuleId.Length));
            foreach (var v in r.Violations)
                sb.AppendLine($"{(v.IsNew switch { true => "NEW ", false => "    ", _ => "" })}{Sev(v.Severity),-7} {v.RuleId.PadRight(idWidth)}  {Label(v.Object)}  {Detail(v)}");
        }
        if (r.Truncated) sb.AppendLine($"... {r.Summary.Total - r.Violations.Count} more not shown.");
        AppendProblems(sb, r, markdown: false);
        return sb.ToString();
    }

    public static string Markdown(LintResult r, string? heading = null)
    {
        var sb = new StringBuilder();
        var s = r.Summary;
        sb.AppendLine($"### {heading ?? $"Lint — `{r.Model.Name}`"}");
        sb.AppendLine();
        sb.AppendLine($"{(r.Blocking ? "❌" : "✅")} 🔴 {s.Error} errors · 🟠 {s.Warning} warnings · ⚪ {s.Info} info · " +
                      $"{s.RulesEvaluated} rules evaluated, {s.RulesSkipped} skipped, {s.Suppressed} suppressed");
        sb.AppendLine();

        if (s.New is { } newCount)
        {
            sb.AppendLine($"**{newCount} new violation{(newCount == 1 ? "" : "s")}** since `{r.BaseRef}` · {s.Existing} existing (not blocking)");
            sb.AppendLine();
            AppendGroups(sb, r, r.Violations.Where(v => v.IsNew == true), openErrors: true);
            var old = r.Violations.Where(v => v.IsNew != true).ToList();
            if (old.Count > 0)
            {
                sb.AppendLine($"<details><summary>{old.Count} existing violation{(old.Count == 1 ? "" : "s")} (not blocking)</summary>");
                sb.AppendLine();
                AppendTable(sb, r, old);
                sb.AppendLine("</details>");
                sb.AppendLine();
            }
        }
        else AppendGroups(sb, r, r.Violations, openErrors: true);

        if (r.Truncated) sb.AppendLine($"_{r.Summary.Total - r.Violations.Count} more violations not shown._");
        AppendProblems(sb, r, markdown: true);
        return sb.ToString();
    }

    private static void AppendGroups(StringBuilder sb, LintResult r, IEnumerable<Violation> violations, bool openErrors)
    {
        foreach (var group in violations.GroupBy(v => v.Severity).OrderByDescending(g => g.Key))
        {
            var open = openErrors && group.Key == Severity.Error;
            sb.AppendLine(open ? $"#### {Icon(group.Key)} {Title(group.Key)} ({group.Count()})" : $"<details><summary>{Icon(group.Key)} {Title(group.Key)} ({group.Count()})</summary>");
            sb.AppendLine();
            AppendTable(sb, r, group);
            if (!open) { sb.AppendLine("</details>"); sb.AppendLine(); }
        }
    }

    private static void AppendTable(StringBuilder sb, LintResult r, IEnumerable<Violation> violations)
    {
        sb.AppendLine("| Rule | Object | Detail |");
        sb.AppendLine("|---|---|---|");
        foreach (var v in violations)
        {
            var info = r.Rules.GetValueOrDefault(v.RuleId);
            var rule = info?.HelpUrl is { } url ? $"[{Cell(info.Name)}]({url})" : Cell(info?.Name ?? v.RuleId);
            sb.AppendLine($"| {rule}<br>`{Cell(v.RuleId)}` | `{Cell(Label(v.Object))}` | {Cell(Detail(v))} |");
        }
        sb.AppendLine();
    }

    public static string RulesText(IEnumerable<(Ruleset Ruleset, RuleInfo Rule)> rules)
    {
        var sb = new StringBuilder();
        foreach (var group in rules.GroupBy(x => x.Ruleset.Id))
        {
            var list = group.ToList();
            sb.AppendLine($"{group.Key} ({list[0].Ruleset.Source}, {Count(list.Count, "rule")}){(list[0].Ruleset.Path is { } p ? "  " + p : "")}");
            var width = Math.Min(60, list.Max(x => x.Rule.Id.Length));
            foreach (var (_, rule) in list)
                sb.AppendLine($"  {Sev(rule.Severity),-7} {rule.Id.PadRight(width)}  {rule.Name}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    public static string DependenciesText(DependencyResult d)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{d.Object.Type} {Label(d.Object)}");
        void Edges(string title, IReadOnlyList<DependencyEdge> edges, bool upstream)
        {
            sb.AppendLine();
            sb.AppendLine($"{title} ({edges.Count})");
            foreach (var e in edges.OrderBy(e => e.Depth).ThenBy(e => (upstream ? e.To : e.From).Key, StringComparer.Ordinal))
            {
                var other = upstream ? e.To : e.From;
                var via = e.Via == "dax" ? (e.Part is { } p && p != ExpressionPart.Expression ? $"dax ({p.ToString().ToLowerInvariant()})" : "dax") : e.Via;
                var through = e.Depth > 1 ? $"  via {Label(upstream ? e.From : e.To)}" : "";
                sb.AppendLine($"  {new string(' ', (e.Depth - 1) * 2)}{other.Type} {Label(other)}  [{via}]{through}");
            }
        }
        Edges("Depends on", d.Upstream, upstream: true);
        Edges("Used by", d.Downstream, upstream: false);
        if (d.Unresolved.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Unresolved references ({d.Unresolved.Count})");
            foreach (var u in d.Unresolved) sb.AppendLine($"  {u.Text}  (line {u.Line})");
        }
        if (d.Truncated) sb.AppendLine($"Stopped after {DependencyQuery.MaxEdges} edges.");
        return sb.ToString();
    }

    internal static string Label(ObjectRef o) => o.Type switch
    {
        "column" or "measure" => $"{Describe.ModelDescriber.Quote(o.Table!)}[{o.Name}]",
        "table" => Describe.ModelDescriber.Quote(o.Name),
        "model" => "(model)",
        "relationship" => o.Name.Replace("->", " → ", StringComparison.Ordinal),
        "tablePermission" => $"{o.Name} (role {Dax.DependencyQuery.SplitKey(o.Key.Split('/')[0]).Name})",
        _ => o.Table is null ? o.Name : $"{Describe.ModelDescriber.Quote(o.Table)} / {o.Name}",
    };

    private static string Detail(Violation v)
    {
        var colon = v.Message.IndexOf(": ", StringComparison.Ordinal);
        return colon >= 0 ? v.Message[(colon + 2)..] : v.Message;
    }

    private static void AppendProblems(StringBuilder sb, LintResult r, bool markdown)
    {
        if (r.SkippedRules.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(markdown ? $"<details><summary>{r.SkippedRules.Count} rules skipped</summary>\n" : $"Skipped rules ({r.SkippedRules.Count}):");
            foreach (var s in r.SkippedRules) sb.AppendLine(markdown ? $"- `{s.RuleId}` ({s.Ruleset}): {Cell(s.Reason)}" : $"  {s.RuleId} ({s.Ruleset}): {s.Reason}");
            if (markdown) sb.AppendLine("\n</details>");
        }
        if (r.RuleErrors.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(markdown ? "**Rules that failed on some objects:**\n" : "Rules that failed on some objects:");
            foreach (var e in r.RuleErrors) sb.AppendLine(markdown ? $"- `{e.RuleId}` ({e.Objects} objects): {Cell(e.Message)}" : $"  {e.RuleId} ({e.Objects} objects): {e.Message}");
        }
    }

    private static string Sev(Severity s) => s.ToString().ToUpperInvariant();
    private static string Icon(Severity s) => s switch { Severity.Error => "🔴", Severity.Warning => "🟠", _ => "⚪" };
    private static string Title(Severity s) => s switch { Severity.Error => "Errors", Severity.Warning => "Warnings", _ => "Info" };
    private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 || noun == "info" ? "" : "s")}";
    private static string Cell(string value) => value.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal);
}
