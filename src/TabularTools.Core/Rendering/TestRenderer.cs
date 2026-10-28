using System.Text;
using TabularTools.Core.Queries;
using TabularTools.Core.Testing;

namespace TabularTools.Core.Rendering;

/// <summary>Measure test results as text, and as a PR comment section.</summary>
public static class TestRenderer
{
    public const string DefaultMarker = "measure-tests";

    public static string SummaryLine(MeasureTestRunResult r)
    {
        var s = r.Summary;
        var sides = r.Base is null || r.Head is null ? "" : $" ({r.Base.Label} → {r.Head.Label})";
        var structural = r.Files.Sum(f => f.StructuralChangesNotTested.Count);
        return $"Measure tests{sides}: {Counts(s)}" +
               (structural > 0 ? $"; {structural} structural change{(structural == 1 ? "" : "s")} not tested" : "") +
               $" — {(r.Blocking ? "blocking" : "not blocking")}.";
    }

    public static string Counts(TestSummary s)
    {
        var parts = new List<string> { $"{s.Passed} passed" };
        if (s.Failed > 0) parts.Add($"{s.Failed} failed");
        if (s.Changed > 0) parts.Add($"{s.Changed} changed as expected");
        if (s.Errored > 0) parts.Add($"{s.Errored} errored");
        if (s.Skipped > 0) parts.Add($"{s.Skipped} skipped");
        return string.Join(", ", parts);
    }

    public static string Text(MeasureTestRunResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine(SummaryLine(r));
        sb.AppendLine($"Target: {string.Join(", ", r.Targets.Select(t => $"{t.Endpoint} / {t.Database}"))}");
        foreach (var f in r.Files)
        {
            sb.AppendLine();
            sb.AppendLine($"{f.File} (model {f.Model}): {Counts(f.Summary)}");
            if (f.Overrides is { } o && (o.Head.Count > 0 || o.Base.Count > 0))
                sb.AppendLine($"  Overrides: base {List(o.Base)}; head {List(o.Head)}");
            foreach (var d in f.Diagnostics ?? []) sb.AppendLine($"  {d.Level.ToString().ToLowerInvariant()} {d.Code}: {d.Message}");
            foreach (var a in f.Results)
            {
                sb.AppendLine($"  {Tag(a.Status),-8} {a.CaseId} [{a.Assertion}]{(a.Role is null ? "" : $" as {a.Role}")}{(a.Message is null ? "" : " — " + a.Message)}");
                foreach (var d in a.Diffs ?? []) sb.AppendLine($"           {ResultComparer.Describe(d.Key)}: [{d.Measure}] {DaxValues.Format(d.Base)} → {DaxValues.Format(d.Head)}{Delta(d)}");
                if (a.DiffCount > (a.Diffs?.Count ?? 0)) sb.AppendLine($"           … {a.DiffCount - a.Diffs!.Count} more");
            }
            if (f.StructuralChangesNotTested.Count > 0)
            {
                sb.AppendLine("  Not tested (structural changes queries can't simulate):");
                foreach (var c in f.StructuralChangesNotTested) sb.AppendLine($"    - {DiffRenderer.Label(c)}: {DiffRenderer.KindPhrase(c.Kind)}");
            }
        }
        return sb.ToString();
    }

    /// <summary>A PR comment section. <paramref name="headingLevel"/> 3 for a standalone comment, 4 inside a review.</summary>
    public static string Markdown(MeasureTestRunResult r, string? marker = DefaultMarker, int headingLevel = 3)
    {
        var h = new string('#', headingLevel);
        var sb = new StringBuilder();
        if (marker is not null) sb.AppendLine(DiffRenderer.Marker(marker));
        var sides = r.Base is null || r.Head is null ? "" : $" · base `{r.Base.Label}` → head `{r.Head.Label}`";
        sb.AppendLine($"{h} {(r.Blocking ? "❌" : "✅")} Measure tests{sides}");
        sb.AppendLine();
        sb.AppendLine($"{Counts(r.Summary)}. Evaluated on {string.Join(", ", r.Targets.Select(t => $"`{DiffRenderer.Escape(t.Database)}`"))} with query-scoped overrides; nothing was deployed.");
        foreach (var f in r.Files)
        {
            sb.AppendLine();
            sb.AppendLine($"**`{f.File}`** — {Counts(f.Summary)}");
            if (f.Overrides is { DirectlyEdited.Count: > 0 } o) sb.AppendLine($"Edited measures: {string.Join(", ", o.DirectlyEdited.Select(x => $"`{DiffRenderer.Escape(x)}`"))}");
            foreach (var d in f.Diagnostics ?? []) sb.AppendLine($"> ℹ️ {DiffRenderer.Escape(d.Message)}");
            var notable = f.Results.Where(a => a.Status != TestStatus.Pass).ToList();
            if (notable.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("| | Case | Assertion | Result |");
                sb.AppendLine("|---|---|---|---|");
                foreach (var a in notable)
                    sb.AppendLine($"| {Icon(a.Status)} | `{a.CaseId}` | {a.Assertion}{(a.Role is null ? "" : $" (as {DiffRenderer.Escape(a.Role)})")} | {DiffRenderer.Escape(a.Message ?? Tag(a.Status))} |");
                foreach (var a in notable.Where(a => a.Diffs is { Count: > 0 }))
                {
                    var open = a.Status == TestStatus.Fail ? " open" : "";
                    sb.AppendLine();
                    sb.AppendLine($"<details{open}><summary>{Icon(a.Status)} <code>{DiffRenderer.Escape(a.CaseId)}</code>: {a.DiffCount} value{(a.DiffCount == 1 ? "" : "s")} differ</summary>");
                    sb.AppendLine();
                    sb.AppendLine($"| Row | Measure | {(a.Assertion == "snapshot" ? "Stored" : "Base")} | {(a.Assertion == "snapshot" ? "Live" : "Head")} | Δ |");
                    sb.AppendLine("|---|---|---:|---:|---:|");
                    foreach (var d in a.Diffs!)
                        sb.AppendLine($"| {DiffRenderer.Escape(ResultComparer.Describe(d.Key))} | {(d.Expected ? "" : "⚠️ ")}{DiffRenderer.Escape(d.Measure)} | {DiffRenderer.Escape(DaxValues.Format(d.Base))} | {DiffRenderer.Escape(DaxValues.Format(d.Head))} | {(d.Delta is { } x ? Number(x) : "")} |");
                    if (a.DiffCount > a.Diffs.Count) sb.AppendLine($"\n… and {a.DiffCount - a.Diffs.Count} more.");
                    sb.AppendLine();
                    sb.AppendLine("</details>");
                }
            }
            if (f.StructuralChangesNotTested.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"<details><summary>⚠️ {f.StructuralChangesNotTested.Count} structural change{(f.StructuralChangesNotTested.Count == 1 ? "" : "s")} not covered by these tests</summary>");
                sb.AppendLine();
                foreach (var c in f.StructuralChangesNotTested) sb.AppendLine($"- {DiffRenderer.Escape(DiffRenderer.Label(c))}: {DiffRenderer.Escape(DiffRenderer.KindPhrase(c.Kind))}");
                sb.AppendLine();
                sb.AppendLine("Queries can't simulate relationship, data type, partition, RLS or calculated table changes; test them on a deployed copy.");
                sb.AppendLine();
                sb.AppendLine("</details>");
            }
        }
        if (headingLevel == 3)
        {
            sb.AppendLine();
            sb.AppendLine(DiffRenderer.Footer());
        }
        return sb.ToString();
    }

    private static string Tag(TestStatus s) => s switch
    {
        TestStatus.Pass => "PASS",
        TestStatus.Fail => "FAIL",
        TestStatus.Changed => "CHANGED",
        TestStatus.Error => "ERROR",
        _ => "SKIPPED",
    };

    private static string Icon(TestStatus s) => s switch
    {
        TestStatus.Pass => "✅",
        TestStatus.Fail => "❌",
        TestStatus.Changed => "🔵",
        TestStatus.Error => "⚠️",
        _ => "⏭️",
    };

    private static string List(IReadOnlyList<string> names) => names.Count == 0 ? "(none)" : string.Join(", ", names);

    private static string Delta(CellDiff d) => d.Delta is { } x ? $" (Δ {Number(x)})" : "";

    private static string Number(double x) => (x > 0 ? "+" : "") + x.ToString("G10", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Query results as a text or Markdown table.</summary>
public static class DaxResultRenderer
{
    public static string SummaryLine(DaxResult r) =>
        $"{r.RowCount} row{(r.RowCount == 1 ? "" : "s")}{(r.Truncated ? " (truncated)" : "")} from {r.Target.Database} in {r.DurationMs} ms{(r.Recorded ? " (recorded)" : "")}.";

    public static string Text(DaxResult r)
    {
        var headers = r.Columns.Select(c => c.Name).ToList();
        var cells = r.Rows.Select(row => row.Select(DaxValues.Format).ToList()).ToList();
        var widths = headers.Select((h, i) => Math.Min(60, Math.Max(h.Length, cells.Count == 0 ? 0 : cells.Max(c => c[i].Length)))).ToList();
        var sb = new StringBuilder();
        sb.AppendLine(string.Join("  ", headers.Select((h, i) => Fit(h, widths[i]))));
        sb.AppendLine(string.Join("  ", widths.Select(w => new string('-', w))));
        foreach (var row in cells) sb.AppendLine(string.Join("  ", row.Select((c, i) => Fit(c, widths[i]))));
        sb.AppendLine(SummaryLine(r));
        return sb.ToString();
    }

    public static string Markdown(DaxResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("| " + string.Join(" | ", r.Columns.Select(c => DiffRenderer.Escape(c.Name))) + " |");
        sb.AppendLine("|" + string.Concat(r.Columns.Select(c => c.DataType is "string" or "boolean" or "dateTime" ? "---|" : "---:|")));
        foreach (var row in r.Rows) sb.AppendLine("| " + string.Join(" | ", row.Select(v => DiffRenderer.Escape(DaxValues.Format(v)))) + " |");
        sb.AppendLine();
        sb.AppendLine(SummaryLine(r));
        return sb.ToString();
    }

    private static string Fit(string text, int width) => text.Length > width ? text[..(width - 1)] + "…" : text.PadRight(width);
}
