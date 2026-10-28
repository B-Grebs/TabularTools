using System.Text;
using TabularTools.Core.Lint;
using TabularTools.Core.Review;

namespace TabularTools.Core.Rendering;

/// <summary>The review as one PR comment (ARCHITECTURE §10.7), and as plain text.</summary>
public static class ReviewRenderer
{
    public const string DefaultMarker = "review";

    public static string SummaryLine(ReviewResult r)
    {
        if (r.Models.Count == 0) return $"No semantic models changed between {r.Base.Label} and {r.Head.Label}.";
        var parts = r.Models.Select(m =>
        {
            var lint = m.Lint is { Summary: var s } ? $", lint {s.New ?? s.Total} new" : "";
            return $"'{m.Diff.Name}' {DiffRenderer.Counts(m.Diff.Summary, plain: true)}{lint}";
        });
        return $"{r.Base.Label} → {r.Head.Label}: {string.Join("; ", parts)} — {(r.Blocking ? "blocking" : "not blocking")}.";
    }

    public static string Text(ReviewResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine(SummaryLine(r));
        foreach (var m in r.Models)
        {
            sb.AppendLine();
            sb.Append(DiffRenderer.Text(new Diff.DiffResult
            {
                Base = r.Base, Head = r.Head, Models = [m.Diff], FailOn = r.DiffFailOn, Blocking = false,
            }).Split('\n', 2)[1].TrimStart('\n'));
            if (m.Lint is { } lint)
            {
                sb.AppendLine();
                sb.Append(LintRenderer.Text(lint with { Violations = lint.Violations.Where(v => v.IsNew != false).ToList() }));
            }
        }
        return sb.ToString();
    }

    public static string Markdown(ReviewResult r, string? marker = DefaultMarker)
    {
        var sb = new StringBuilder();
        if (marker is not null) sb.AppendLine(DiffRenderer.Marker(marker));
        if (r.Models.Count == 0)
        {
            sb.AppendLine($"### Semantic model review · base `{r.Base.Label}` → head `{r.Head.Label}`");
            sb.AppendLine();
            sb.AppendLine("No semantic model changes.");
        }
        foreach (var m in r.Models)
        {
            sb.Append(DiffRenderer.ModelMarkdown(m.Diff, r.Base, r.Head));
            if (m.Lint is { } lint) AppendLint(sb, lint);
        }
        if (r.Diagnostics is { Count: > 0 } diagnostics)
            foreach (var d in diagnostics) sb.AppendLine($"> ⚠️ {DiffRenderer.Escape(d.Message)}");
        sb.AppendLine();
        sb.AppendLine($"{(r.Blocking ? "❌ **Blocking findings.**" : "✅ Nothing blocking.")} {DiffRenderer.Footer()}");
        return sb.ToString();
    }

    private static void AppendLint(StringBuilder sb, LintResult lint)
    {
        var s = lint.Summary;
        var newCount = s.New ?? s.Total;
        var existing = s.Existing ?? 0;
        var title = $"Lint: {newCount} new violation{(newCount == 1 ? "" : "s")}" + (existing > 0 ? $" ({existing} existing, not blocking)" : "");
        // Expanded when new violations block the build; collapsed otherwise.
        var body = LintRenderer.Markdown(lint, heading: title);
        if (lint.Blocking) sb.AppendLine(body.Replace("### ", "#### ", StringComparison.Ordinal));
        else
        {
            var withoutHeading = body[(body.IndexOf('\n', StringComparison.Ordinal) + 1)..];
            sb.AppendLine($"<details><summary>{(newCount > 0 ? "🟠" : "✅")} {title}</summary>");
            sb.AppendLine(withoutHeading.Replace("#### ", "##### ", StringComparison.Ordinal));
            sb.AppendLine("</details>");
            sb.AppendLine();
        }
    }
}
