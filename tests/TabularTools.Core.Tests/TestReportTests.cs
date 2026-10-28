using TabularTools.Core.Rendering;
using TabularTools.Core.Review;
using TabularTools.Core.Testing;

namespace TabularTools.Core.Tests;

public sealed class TestReportTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Pr_comment_for_the_v1_to_v2_tests_matches_the_golden_file()
    {
        using var repo = MeasureTestRunnerTests.DiffRepo();
        var result = await MeasureTestRunner.RunAsync(new TestRunRequest { TestFiles = [repo.Combine("sales.measuretests.yaml")], Base = "main", WorkingDirectory = repo.Path }, new NoLiveExecutors(), Ct);
        Golden.Assert("ExampleSales.tests.md", TestRenderer.Markdown(result), repo);

        var text = TestRenderer.Text(result);
        Assert.Contains("FAIL     sales-by-year [differential] — Changed without being edited: [Margin %] (2 rows).", text, StringComparison.Ordinal);
        Assert.Contains("Date[Year] = 2023: [Margin %] 0.312 → 0.3155", text, StringComparison.Ordinal);
        Assert.StartsWith("Measure tests (", TestRenderer.SummaryLine(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Review_runs_the_configured_tests_and_blocks_on_regressions()
    {
        using var repo = MeasureTestRunnerTests.DiffRepo();
        var review = await ReviewEngine.RunAsync(new ReviewRequest
        {
            Diff = new Diff.DiffRequest { Base = "main", Head = "HEAD", WorkingDirectory = repo.Path },
            NoLint = true,
            Tests = new ReviewTestOptions(new NoLiveExecutors()),
        }, Ct);

        Assert.NotNull(review.Tests);
        Assert.Equal(1, review.Tests.Summary.Failed);
        Assert.True(review.Blocking);
        var markdown = ReviewRenderer.Markdown(review);
        Assert.Contains("#### ❌ Measure tests", markdown, StringComparison.Ordinal);
        Assert.Contains("measure tests 2 passed, 1 failed, 1 changed as expected", ReviewRenderer.SummaryLine(review), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Review_without_a_profile_says_why_tests_were_skipped()
    {
        using var repo = MeasureTestRunnerTests.DiffRepo();
        File.WriteAllText(repo.Combine("tabulartools.yaml"), "tests:\n  files: [\"*.measuretests.yaml\"]\n");
        var review = await ReviewEngine.RunAsync(new ReviewRequest
        {
            Diff = new Diff.DiffRequest { Base = "main", WorkingDirectory = repo.Path },
            NoLint = true,
            Tests = new ReviewTestOptions(new NoLiveExecutors(), Environment: _ => null),
        }, Ct);
        Assert.Null(review.Tests);
        Assert.Contains("no connection profile", review.TestsSkipped, StringComparison.Ordinal);
        Assert.Contains("Measure tests skipped", ReviewRenderer.Markdown(review), StringComparison.Ordinal);
    }
}
