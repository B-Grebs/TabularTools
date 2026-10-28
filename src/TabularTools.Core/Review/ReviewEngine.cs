using TabularTools.Core.Diagnostics;
using TabularTools.Core.Diff;
using TabularTools.Core.Lint;
using TabularTools.Core.Queries;
using TabularTools.Core.Testing;

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
    /// <summary>Measure test results, when the config lists test files and a profile is available.</summary>
    public MeasureTestRunResult? Tests { get; init; }
    /// <summary>Why measure tests didn't run, when test files exist but couldn't be run.</summary>
    public string? TestsSkipped { get; init; }
    /// <summary>True when the diff reaches its fail-on level, any model has new blocking lint violations, or measure tests fail.</summary>
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
    /// <summary>Runs measure tests listed in tests.files (differential, same base and head). Null: don't run them.</summary>
    public ReviewTestOptions? Tests { get; init; }
}

public sealed record ReviewTestOptions(IDaxExecutorFactory Factory, string? Profile = null, bool? Strict = null, Func<string, string?>? Environment = null);

/// <summary>
/// The pipeline's one-stop check (CLI <c>review</c>): semantic diff of every changed model, plus diff-aware lint of each
/// head version, plus the measure tests configured under tests.files. Lineage (M5) will join here.
/// </summary>
public static class ReviewEngine
{
    public static ReviewResult Run(ReviewRequest request) => RunAsync(request).GetAwaiter().GetResult();

    public static async Task<ReviewResult> RunAsync(ReviewRequest request, CancellationToken cancellationToken = default)
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

        MeasureTestRunResult? tests = null;
        string? skipped = null;
        if (request.Tests is { } t && MeasureTestRunner.FindTestFiles(run.Config) is { Count: > 0 } files)
        {
            try
            {
                tests = await MeasureTestRunner.RunAsync(new TestRunRequest
                {
                    TestFiles = files,
                    Profile = t.Profile,
                    Base = request.Diff.Base,
                    Head = request.Diff.Head ?? "HEAD",
                    MergeBase = request.Diff.MergeBase,
                    Strict = t.Strict,
                    ConfigPath = run.Config?.Path,
                    WorkingDirectory = run.Git?.RepositoryRoot ?? request.Diff.WorkingDirectory,
                    Environment = t.Environment,
                }, t.Factory, cancellationToken).ConfigureAwait(false);
            }
            catch (ArgumentException ex) when (ex.Message.StartsWith("No connection profile", StringComparison.Ordinal))
            {
                skipped = $"{files.Count} measure test file{(files.Count == 1 ? "" : "s")} found, but no connection profile is set (tests.profile or TABULARTOOLS_PROFILE).";
            }
        }

        return new ReviewResult
        {
            Base = diff.Base,
            Head = diff.Head,
            MergeBase = diff.MergeBase,
            Models = models,
            DiffFailOn = diff.FailOn,
            Tests = tests,
            TestsSkipped = skipped,
            Blocking = diff.Blocking || models.Any(m => m.Lint?.Blocking == true) || tests?.Blocking == true,
            Diagnostics = diff.Diagnostics,
        };
    }
}
