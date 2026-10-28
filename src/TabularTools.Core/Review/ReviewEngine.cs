using TabularTools.Core.Diagnostics;
using TabularTools.Core.Diff;
using TabularTools.Core.Lint;

namespace TabularTools.Core.Review;

/// <summary>One model in a review: its semantic diff and the lint result for its head version (new violations marked).</summary>
public sealed record ReviewModel(ModelDiff Diff, LintResult? Lint);

public sealed record ReviewResult
{
    public int SchemaVersion { get; init; } = 1;
    public required DiffSide Base { get; init; }
    public required DiffSide Head { get; init; }
    public bool MergeBase { get; init; }
    public required IReadOnlyList<ReviewModel> Models { get; init; }
    public required DiffFailOn DiffFailOn { get; init; }
    /// <summary>True when the diff reaches its fail-on level or any model has new blocking lint violations.</summary>
    public required bool Blocking { get; init; }
    public IReadOnlyList<Diagnostic>? Diagnostics { get; init; }
}

public sealed record ReviewRequest
{
    public required DiffRequest Diff { get; init; }
    /// <summary>Rulesets instead of the configured ones.</summary>
    public IReadOnlyList<string>? Rulesets { get; init; }
    public FailOn? LintFailOn { get; init; }
    /// <summary>Skip linting.</summary>
    public bool NoLint { get; init; }
}

/// <summary>
/// The pipeline's one-stop check (CLI <c>review</c>): semantic diff of every changed model, plus diff-aware lint of each
/// head version. Measure tests (M4) and lineage (M5) will join here.
/// </summary>
public static class ReviewEngine
{
    public static ReviewResult Run(ReviewRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var run = SemanticDiff.Run(request.Diff);
        var diff = run.Result;
        var models = new List<ReviewModel>();
        foreach (var comparison in run.Models)
        {
            LintResult? lint = null;
            if (!request.NoLint && comparison.Head is { } head)
            {
                var options = new LintOptions
                {
                    Config = run.Config,
                    Rulesets = request.Rulesets,
                    FailOn = request.LintFailOn,
                    BaseDirectory = run.Git?.RepositoryRoot ?? request.Diff.WorkingDirectory,
                };
                lint = DiffAwareLint.Run(head.Model, comparison.Base?.Model, options, request.Diff.Base, diff.Base.Commit ?? diff.Base.Label);
            }
            models.Add(new ReviewModel(comparison.Diff, lint));
        }

        return new ReviewResult
        {
            Base = diff.Base,
            Head = diff.Head,
            MergeBase = diff.MergeBase,
            Models = models,
            DiffFailOn = diff.FailOn,
            Blocking = diff.Blocking || models.Any(m => m.Lint?.Blocking == true),
            Diagnostics = diff.Diagnostics,
        };
    }
}
