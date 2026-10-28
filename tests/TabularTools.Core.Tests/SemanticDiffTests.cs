using TabularTools.Core.Diff;
using TabularTools.Core.Rendering;

namespace TabularTools.Core.Tests;

public class SemanticDiffTests
{
    private static DiffRun Run(GitFixture repo, string @base, string? head = null, string? model = null, bool mergeBase = true) =>
        SemanticDiff.Run(new DiffRequest { Base = @base, Head = head, ModelPath = model, MergeBase = mergeBase, WorkingDirectory = repo.Path });

    [Fact]
    public void Diffs_every_changed_model_between_two_refs()
    {
        using var repo = new GitFixture();
        var result = Run(repo, "main").Result;

        Assert.Equal(DiffSideKind.Ref, result.Base.Kind);
        Assert.Equal(repo.MainCommit, result.Base.Commit);
        Assert.Equal(repo.FeatureCommit, result.Head.Commit);
        Assert.True(result.MergeBase);
        var model = Assert.Single(result.Models);
        Assert.Equal(GitFixture.ModelPath, model.ModelPath);
        Assert.Equal("ExampleSales", model.Name);
        Assert.Equal(ModelDiffStatus.Modified, model.Status);
        Assert.Equal(new DiffSummary(5, 4, 2, 1, 1, 13, 1), model.Summary);
        Assert.False(result.Blocking); // diff.failOn defaults to none
        Assert.Equal("ExampleSales.SemanticModel/definition/tables/Sales.tmdl",
            model.Changes.Single(c => c.Kind == "measure.expressionChanged").Location!.File);
    }

    [Fact]
    public void Uses_the_merge_base_so_later_changes_on_the_target_branch_are_ignored()
    {
        using var repo = new GitFixture();
        repo.Git("checkout", "-q", "main");
        File.AppendAllText(repo.Combine(GitFixture.ModelPath, "definition", "tables", "Store.tmdl"), "\n");
        File.WriteAllText(repo.Combine(GitFixture.ModelPath, "definition", "expressions.tmdl"),
            File.ReadAllText(repo.Combine(GitFixture.ModelPath, "definition", "expressions.tmdl")).Replace("\"SalesDW\"", "\"SalesDW2\"", StringComparison.Ordinal));
        repo.Commit("main moves on", "2026-01-03T00:00:00Z");
        repo.Git("checkout", "-q", "feature");

        var withMergeBase = Run(repo, "main").Result.Models.Single();
        Assert.DoesNotContain(withMergeBase.Changes, c => c.Kind == "expression.expressionChanged");

        var direct = Run(repo, "main", mergeBase: false).Result.Models.Single();
        Assert.Contains(direct.Changes, c => c.Kind == "expression.expressionChanged");
    }

    [Fact]
    public void Worktree_head_sees_uncommitted_edits()
    {
        using var repo = new GitFixture();
        var sales = repo.Combine(GitFixture.ModelPath, "definition", "tables", "Sales.tmdl");
        File.WriteAllText(sales, File.ReadAllText(sales).Replace("SUM(Sales[Quantity])", "SUM(Sales[Quantity]) + 0", StringComparison.Ordinal));

        var result = Run(repo, "HEAD", SemanticDiff.WorktreeRef).Result;
        Assert.Equal(DiffSideKind.Worktree, result.Head.Kind);
        var change = Assert.Single(Assert.Single(result.Models).Changes);
        Assert.Equal(("measure.expressionChanged", "table:Sales/measure:Units Sold"), (change.Kind, change.Object.Key));
    }

    [Fact]
    public void A_model_added_in_head_is_reported_as_new()
    {
        using var repo = new GitFixture();
        var result = Run(repo, "HEAD~1", model: GitFixture.ModelPath).Result; // same diff, explicit model
        Assert.Equal(13, result.Models.Single().Summary.Total);

        repo.Git("mv", GitFixture.ModelPath, "Renamed.SemanticModel");
        repo.Commit("move model", "2026-01-04T00:00:00Z");
        var moved = Run(repo, "HEAD~1").Result;
        Assert.Equal([ModelDiffStatus.Added, ModelDiffStatus.Removed], moved.Models.Select(m => m.Status).Order());
    }

    [Fact]
    public void Two_folders_can_be_compared_without_git()
    {
        var run = SemanticDiff.Run(new DiffRequest
        {
            Base = "diff/ExampleSales-v1/ExampleSales.SemanticModel",
            Head = "diff/ExampleSales-v2/ExampleSales.SemanticModel",
            FailOn = DiffFailOn.Breaking,
            WorkingDirectory = TestPaths.Fixtures,
        });
        Assert.Equal(DiffSideKind.Folder, run.Result.Base.Kind);
        Assert.True(run.Result.Blocking);
        Assert.Null(run.Git);
        Assert.Equal(13, run.Result.Models.Single().Summary.Total);
    }

    [Fact]
    public void Config_sets_fail_on_and_severity_overrides()
    {
        using var repo = new GitFixture();
        File.WriteAllText(repo.Combine("tabulartools.yaml"), """
            diff:
              failOn: high
              severity:
                relationship.crossFilterChanged: low
                relationship.cardinalityChanged: low
                column.dataTypeChanged: low
                measure.expressionChanged: low
                role.rlsFilterChanged: medium
                measure.renamed: medium
                column.renamed: medium
                measure.moved: medium
                measure.removed: medium
            """);
        var result = Run(repo, "main").Result;
        Assert.Equal(DiffFailOn.High, result.FailOn);
        Assert.False(result.Blocking);
        Assert.Equal(0, result.Models.Single().Summary.High);
        Assert.EndsWith("tabulartools.yaml", result.ConfigPath);
    }

    [Fact]
    public void A_shallow_or_unknown_base_fails_with_a_git_error()
    {
        using var repo = new GitFixture();
        Assert.Throws<Git.GitException>(() => Run(repo, "origin/main"));
    }

    [Fact]
    public void Golden_markdown_matches()
    {
        using var repo = new GitFixture();
        var result = Run(repo, "main").Result;
        Golden.Assert("ExampleSales.diff.md", DiffRenderer.Markdown(result), repo);
    }
}
