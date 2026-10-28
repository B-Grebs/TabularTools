using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TabularTools.Core.Config;
using TabularTools.Core.Dax;
using TabularTools.Core.Lint;
using TabularTools.Core.Loading;
using TabularTools.Core.Rendering;

namespace TabularTools.Mcp.Tools;

/// <summary>Read-only analysis tools: best-practice lint and DAX dependencies.</summary>
[McpServerToolType]
public sealed class LintTools(ModelWorkspace workspace)
{
    [McpServerTool(Name = "lint_model", Title = "Lint semantic model", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(LintResult))]
    [Description("Check a semantic model against best-practice rules — Microsoft's Best Practice Analyzer rules, built-in TT rules (broken DAX references, " +
                 "ambiguous filter paths, RLS mistakes, hard-coded sources, name collisions) and any team rule files — and return violations with rule, " +
                 "object, severity and a fix hint. Settings come from the nearest tabulartools.yaml. Rules that can't be evaluated are listed in skippedRules. " +
                 "Pass baseRef (e.g. 'main') for PR review: violations that already existed at that git ref are marked isNew=false and don't block.")]
    public CallToolResult LintModel(
        [Description(ModelTools.ModelParameterDescription)] string? model = null,
        [Description("Rulesets to run instead of the configured ones: 'builtin', 'microsoft-bpa', or paths to BPA JSON rule files.")] string[]? rulesets = null,
        [Description("Only report violations at or above this severity.")] Severity? minSeverity = null,
        [Description("Only run these rule ids.")] string[]? ruleIds = null,
        [Description("Path to a tabulartools.yaml to use instead of discovering one.")] string? config = null,
        [Description("Maximum violations to return (most severe first). The summary always counts all of them.")] int limit = 200,
        [Description("Git ref to compare with (e.g. 'main'). Violations already present there are marked isNew=false; only new ones block.")] string? baseRef = null,
        [Description("With baseRef: compare with merge-base(baseRef, HEAD), as a pull request does. Default: true.")] bool mergeBase = true) => ToolResults.Run(() =>
    {
        var opened = workspace.Open(model);
        var cfg = ConfigLoader.ForModel(opened.Model.Source, config is null ? null : Path.GetFullPath(Path.Combine(workspace.Roots[0], config)));
        var options = new LintOptions
        {
            Rulesets = rulesets,
            MinSeverity = minSeverity,
            RuleIds = ruleIds,
            Config = cfg,
            BaseDirectory = workspace.Roots[0],
        };
        var result = string.IsNullOrWhiteSpace(baseRef)
            ? LintEngine.Run(opened.Model, options, opened.Handle)
            : DiffAwareLint.Run(opened.Model, options, baseRef, workspace.Roots[0], mergeBase, opened.Handle);

        if (result.Violations.Count > Math.Max(1, limit))
            result = result with { Violations = result.Violations.Take(Math.Max(1, limit)).ToList(), Truncated = true };
        return ToolResults.Success(result, LintRenderer.SummaryLine(result));
    });

    [McpServerTool(Name = "get_dependencies", Title = "Get DAX dependencies", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(DependencyResult))]
    [Description("Show what a measure, column, table, calculation item or DAX function depends on (upstream) and what depends on it (downstream), " +
                 "resolved from every DAX expression in the model plus relationships, sort-by columns and hierarchies. " +
                 "Use before renaming, deleting or changing an object to see what would break. Also lists references in the object's own DAX that don't resolve.")]
    public CallToolResult GetDependencies(
        [Description("The object: Table[Column], 'Table Name'[Column], [Measure], 'Table', a bare measure/table name, or an object key from describe_model.")] string @object,
        [Description(ModelTools.ModelParameterDescription)] string? model = null,
        [Description("upstream = what it uses; downstream = what uses it; both.")] DependencyDirection direction = DependencyDirection.Both,
        [Description("How many levels to follow; 0 = all.")] int depth = 1) => ToolResults.Run(() =>
    {
        var opened = workspace.Open(model);
        var result = DependencyQuery.Query(opened.Model.Snapshot, opened.Model.Dependencies, @object, direction, depth);
        var summary = $"{result.Object.Type} {result.Object.Key}: depends on {result.Upstream.Count}, used by {result.Downstream.Count}" +
                      (result.Unresolved.Count > 0 ? $", {result.Unresolved.Count} unresolved references." : ".");
        return ToolResults.Success(result, summary);
    });
}
