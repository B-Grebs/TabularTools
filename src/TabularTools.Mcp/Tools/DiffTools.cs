using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TabularTools.Core.Diff;
using TabularTools.Core.Loading;
using TabularTools.Core.Rendering;

namespace TabularTools.Mcp.Tools;

/// <summary>Semantic diff between two versions of a model.</summary>
[McpServerToolType]
public sealed class DiffTools(ModelWorkspace workspace)
{
    [McpServerTool(Name = "diff_model", Title = "Diff semantic model versions", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(DiffResult))]
    [Description("Compare two versions of a semantic model by meaning, not text — between two git refs (e.g. a PR's target branch and HEAD), against the " +
                 "working tree (head 'WORKTREE', to check uncommitted edits), or between two model folders. Reports changed DAX, relationship direction and " +
                 "cardinality, data types, summarize-by, RLS filters, and added/removed/renamed/moved objects, each with a severity (breaking, high, medium, low, info), " +
                 "the objects affected downstream, and the TMDL file and line. Reads git locally; never fetches or checks out.")]
    public CallToolResult DiffModel(
        [Description("Base: a git ref such as 'main' or 'origin/main', or a model folder for folder diffs.")] string @base,
        [Description("Head: a git ref, 'WORKTREE' for the files on disk, or a model folder. Default: HEAD.")] string head = "HEAD",
        [Description("Repository folder, relative to the server root. Default: the server root.")] string? repo = null,
        [Description("Only this model: a *.SemanticModel folder, .pbip or model.bim (repo-relative or relative to the server root). Default: every model changed between base and head.")] string? modelPath = null,
        [Description("Compare against merge-base(base, head), as a pull request does. Default: true.")] bool mergeBase = true,
        [Description("Only report changes at or above this severity.")] ChangeSeverity? minSeverity = null,
        [Description("Also report expression changes that only touch whitespace or comments.")] bool includeFormattingOnly = false,
        [Description("Also return each model's PR-comment Markdown in 'markdown'.")] bool markdown = false,
        [Description("Maximum changes to return per model (most severe first). Summaries always count all of them.")] int limit = 200) => ToolResults.Run(() =>
    {
        var run = SemanticDiff.Run(new DiffRequest
        {
            Base = @base,
            Head = head,
            Repo = repo,
            ModelPath = modelPath,
            MergeBase = mergeBase,
            MinSeverity = minSeverity,
            IncludeFormattingOnly = includeFormattingOnly ? true : null,
            WorkingDirectory = workspace.Roots[0],
        });
        var result = run.Result;
        var max = Math.Max(1, limit);
        result = result with
        {
            Models = result.Models.Select(m => m with
            {
                Markdown = markdown ? DiffRenderer.ModelMarkdown(m, result.Base, result.Head) : null,
                Changes = m.Changes.Take(max).ToList(),
            }).ToList(),
        };
        return ToolResults.Success(result, DiffRenderer.SummaryLine(run.Result));
    });

    [McpServerResource(UriTemplate = "tabulartools://diff/change-kinds", Name = "change-kinds", Title = "Diff change kinds", MimeType = "application/json")]
    [Description("Every change kind diff_model reports, with its default severity and meaning. Teams can override severities per kind in tabulartools.yaml (diff.severity).")]
    public static string ChangeKinds() => OutputJson.Serialize(SeverityPolicy.Kinds);
}
