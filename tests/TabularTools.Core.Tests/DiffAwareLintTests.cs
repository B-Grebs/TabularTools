using TabularTools.Core.Diff;
using TabularTools.Core.Lint;
using TabularTools.Core.Loading;
using TabularTools.Core.Rendering;
using TabularTools.Core.Review;

namespace TabularTools.Core.Tests;

public class DiffAwareLintTests
{
    [Fact]
    public void Only_violations_introduced_since_the_base_are_new_and_blocking()
    {
        using var repo = new GitFixture();
        var head = ModelLoader.Load(repo.Combine(GitFixture.ModelPath));

        var result = DiffAwareLint.Run(head, new LintOptions(), "main", repo.Path);

        Assert.Equal("main", result.BaseRef);
        Assert.Equal(repo.MainCommit, result.BaseCommit);
        Assert.Equal(result.Summary.Total, result.Summary.New + result.Summary.Existing);
        Assert.All(result.Violations, v => Assert.NotNull(v.IsNew));
        Assert.Contains(result.Violations, v => v.IsNew == true && v.RuleId == "PROVIDE_FORMAT_STRING_FOR_MEASURES" && v.Object.Key == "table:Promotion/measure:Promotion Count");
        Assert.Contains(result.Violations, v => v.IsNew == false && v.RuleId == "AVOID_FLOATING_POINT_DATA_TYPES" && v.Object.Key == "table:Sales/column:Amount");
        Assert.True(result.Blocking);
        // New violations come first.
        Assert.Equal(result.Violations.OrderByDescending(v => v.IsNew == true).Select(v => v.IsNew), result.Violations.Select(v => v.IsNew));
    }

    [Fact]
    public void A_renamed_object_keeps_its_existing_violations()
    {
        using var repo = new GitFixture();
        var head = ModelLoader.Load(repo.Combine(GitFixture.ModelPath));
        var result = DiffAwareLint.Run(head, new LintOptions { Rulesets = ["microsoft-bpa"] }, "main", repo.Path);

        // 'Total Quantity' (no description) became 'Units Sold' via its lineage tag: the same violation, not a new one.
        var renamed = result.Violations.Where(v => v.Object.Key == "table:Sales/measure:Units Sold").ToList();
        Assert.NotEmpty(renamed);
        Assert.All(renamed, v => Assert.False(v.IsNew));
    }

    [Fact]
    public void Existing_violations_alone_do_not_block()
    {
        using var repo = new GitFixture();
        var head = ModelLoader.Load(repo.Combine(GitFixture.ModelPath));
        var result = DiffAwareLint.Run(head, new LintOptions(), "HEAD", repo.Path);
        Assert.Equal(0, result.Summary.New);
        Assert.False(result.Blocking);
        Assert.Contains("0 new since HEAD", LintRenderer.SummaryLine(result));
        Assert.Contains("existing violations (not blocking)", LintRenderer.Markdown(result));
    }

    [Fact]
    public void Review_combines_the_diff_and_new_lint_violations()
    {
        using var repo = new GitFixture();
        var result = ReviewEngine.Run(new ReviewRequest { Diff = new DiffRequest { Base = "main", WorkingDirectory = repo.Path } });

        var model = Assert.Single(result.Models);
        Assert.Equal(13, model.Diff.Summary.Total);
        Assert.NotNull(model.Lint);
        Assert.True(model.Lint!.Summary.New > 0);
        Assert.True(result.Blocking); // new lint errors

        var noLint = ReviewEngine.Run(new ReviewRequest { Diff = new DiffRequest { Base = "main", WorkingDirectory = repo.Path }, NoLint = true });
        Assert.Null(noLint.Models.Single().Lint);
        Assert.False(noLint.Blocking);
    }

    [Fact]
    public void Golden_review_markdown_matches()
    {
        using var repo = new GitFixture();
        var result = ReviewEngine.Run(new ReviewRequest { Diff = new DiffRequest { Base = "main", WorkingDirectory = repo.Path }, Rulesets = ["builtin"] });
        var markdown = ReviewRenderer.Markdown(result);
        Assert.StartsWith("<!-- tabulartools:review -->", markdown);
        Golden.Assert("ExampleSales.review.md", markdown, repo);
    }
}
