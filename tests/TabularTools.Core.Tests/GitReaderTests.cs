using System.Text;
using TabularTools.Core.Git;

namespace TabularTools.Core.Tests;

public class GitReaderTests
{
    [Fact]
    public void Resolves_refs_merge_base_trees_and_blobs()
    {
        using var repo = new GitFixture();
        var git = GitCliReader.Open(repo.Combine(GitFixture.ModelPath));

        Assert.True(File.Exists(Path.Combine(git.RepositoryRoot, GitFixture.ModelPath, "definition.pbism")));
        Assert.Equal(GitFixture.ModelPath, git.RelativePath(repo.Combine(GitFixture.ModelPath)));
        Assert.Equal(GitFixture.ModelPath + "/definition.pbism", git.RelativePath(repo.Combine(GitFixture.ModelPath, "definition.pbism")));
        Assert.Equal(repo.MainCommit, git.ResolveCommit("main"));
        Assert.Equal(repo.FeatureCommit, git.ResolveCommit("HEAD"));
        Assert.Equal(repo.MainCommit, git.MergeBase(repo.MainCommit, repo.FeatureCommit));
        Assert.False(git.IsShallow);

        var tree = git.ListTree(repo.FeatureCommit, GitFixture.ModelPath);
        var promotion = Assert.Single(tree, e => e.Path == "ExampleSales.SemanticModel/definition/tables/Promotion.tmdl");
        Assert.DoesNotContain(git.ListTree(repo.MainCommit, GitFixture.ModelPath), e => e.Path.EndsWith("Promotion.tmdl", StringComparison.Ordinal));

        var blobs = git.ReadBlobs(tree.Select(e => e.ObjectId));
        Assert.Equal(tree.Select(e => e.ObjectId).Distinct().Count(), blobs.Count);
        Assert.StartsWith("/// Promotions", Encoding.UTF8.GetString(blobs[promotion.ObjectId]));
    }

    [Fact]
    public void Lists_changed_paths_between_commits_and_against_the_working_tree()
    {
        using var repo = new GitFixture();
        var git = GitCliReader.Open(repo.Path);

        var changed = git.ChangedPaths(repo.MainCommit, repo.FeatureCommit);
        Assert.Contains("ExampleSales.SemanticModel/definition/tables/Promotion.tmdl", changed);
        Assert.Contains("ExampleSales.SemanticModel/definition/roles/West Region.tmdl", changed);

        File.WriteAllText(repo.Combine("notes.txt"), "untracked");
        Assert.Contains("notes.txt", git.ChangedPaths(repo.FeatureCommit, null));
        Assert.DoesNotContain("notes.txt", git.ChangedPaths(repo.MainCommit, repo.FeatureCommit));
    }

    [Fact]
    public void Unknown_refs_and_non_repositories_are_git_errors_with_hints()
    {
        using var repo = new GitFixture();
        var git = GitCliReader.Open(repo.Path);
        var unknown = Assert.Throws<GitException>(() => git.ResolveCommit("origin/does-not-exist"));
        Assert.Contains("Unknown git ref 'origin/does-not-exist'", unknown.Message);
        Assert.NotNull(unknown.Hint);
        Assert.Throws<GitException>(() => git.ResolveCommit("--output=x"));

        using var plain = new TempDir();
        var notRepo = Assert.Throws<GitException>(() => GitCliReader.Open(plain.Path));
        Assert.Contains("Not a git repository", notRepo.Message);
    }

    [Fact]
    public void Model_paths_are_found_from_changed_files()
    {
        Assert.Equal(["models/Sales.SemanticModel", "model.bim"],
            Diff.ModelSides.ModelPathsFromChanges([
                "models/Sales.SemanticModel/definition/tables/Sales.tmdl",
                "models/Sales.SemanticModel/definition.pbism",
                "models/Sales.Report/report.json",
                "README.md",
                "model.bim",
            ]).Order(StringComparer.Ordinal).Reverse());
    }
}
