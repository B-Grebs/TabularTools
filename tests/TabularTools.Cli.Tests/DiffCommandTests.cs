using System.Text.Json;

namespace TabularTools.Cli.Tests;

public class DiffCommandTests
{
    private static (int Exit, string Out, string Err) RunIn(string workingDirectory, params string[] args) => RunIn(workingDirectory, null, args);

    private static (int Exit, string Out, string Err) RunIn(string workingDirectory, Func<CliContext, CliContext>? configure, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var context = new CliContext(stdout, stderr, false, workingDirectory);
        var exit = CliApp.Run(args, configure?.Invoke(context) ?? context);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void Diff_between_refs_emits_json_and_is_informational_by_default()
    {
        using var repo = new GitFixture();
        var (exit, output, _) = RunIn(repo.Path, "diff", "--base", "main");

        Assert.Equal(ExitCodes.Success, exit);
        using var doc = JsonDocument.Parse(output);
        Assert.Equal(1, doc.RootElement.GetProperty("schemaVersion").GetInt32());
        var model = doc.RootElement.GetProperty("models")[0];
        Assert.Equal("ExampleSales.SemanticModel", model.GetProperty("modelPath").GetString());
        Assert.Equal(5, model.GetProperty("summary").GetProperty("breaking").GetInt32());
        Assert.Contains(model.GetProperty("changes").EnumerateArray(), c => c.GetProperty("kind").GetString() == "measure.renamed"
            && c.GetProperty("rename").GetProperty("confidence").GetString() == "certain");
    }

    [Fact]
    public void Diff_exits_1_at_the_fail_on_level()
    {
        using var repo = new GitFixture();
        Assert.Equal(ExitCodes.BlockingFindings, RunIn(repo.Path, "diff", "--base", "main", "--fail-on", "breaking").Exit);
        Assert.Equal(ExitCodes.Success, RunIn(repo.Path, "diff", "--base", "main", "--fail-on", "breaking", "--min-severity", "high", "--head", "main").Exit);
    }

    [Fact]
    public void Diff_markdown_carries_the_marker_and_severity_sections()
    {
        using var repo = new GitFixture();
        var (_, output, _) = RunIn(repo.Path, "diff", "--base", "main", "-f", "markdown", "--marker", "model-diff");
        Assert.StartsWith("<!-- tabulartools:model-diff -->", output);
        Assert.Contains("#### 🔴 Breaking (5)", output);
        Assert.Contains("```diff", output);
    }

    [Fact]
    public void Diff_text_lists_changes_with_locations()
    {
        var (exit, output, _) = RunIn(TestPaths.Fixtures, "diff", "--base", "diff/ExampleSales-v1/ExampleSales.SemanticModel",
            "--head", "diff/ExampleSales-v2/ExampleSales.SemanticModel", "-f", "text");
        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("BREAKING Sales[Units Sold]  measure renamed (certain: lineage tag)", output);
        Assert.Contains("(diff/ExampleSales-v2/ExampleSales.SemanticModel/definition/tables/Sales.tmdl:6)", output);
    }

    [Fact]
    public void Git_problems_exit_with_the_environment_code()
    {
        using var repo = new GitFixture();
        var (exit, _, err) = RunIn(repo.Path, "diff", "--base", "origin/main");
        Assert.Equal(ExitCodes.EnvironmentError, exit);
        Assert.Contains("Unknown git ref 'origin/main'", err);

        using var plain = new TempDir();
        Assert.Equal(ExitCodes.EnvironmentError, RunIn(plain.Path, "diff", "--base", "main").Exit);
    }

    [Fact]
    public void Diff_requires_a_base()
    {
        Assert.Equal(ExitCodes.UsageError, RunIn(TestPaths.RepoRoot, "diff").Exit);
    }

    [Fact]
    public void Review_writes_markdown_and_blocks_on_new_lint_errors()
    {
        using var repo = new GitFixture();
        var outFile = repo.Combine("out", "review.md");
        var (exit, _, err) = RunIn(repo.Path, "review", "--base", "main", "-f", "markdown", "-o", outFile);

        Assert.Equal(ExitCodes.BlockingFindings, exit);
        Assert.Contains("Wrote", err);
        var markdown = File.ReadAllText(outFile);
        Assert.StartsWith("<!-- tabulartools:review -->", markdown);
        Assert.Contains("Lint: ", markdown);
        Assert.Contains("❌ **Blocking findings.**", markdown);

        Assert.Equal(ExitCodes.Success, RunIn(repo.Path, "review", "--base", "main", "--lint-fail-on", "none").Exit);
        Assert.Equal(ExitCodes.Success, RunIn(repo.Path, "review", "--base", "main", "--no-lint").Exit);
    }

    [Fact]
    public void Lint_with_base_only_blocks_on_new_violations()
    {
        using var repo = new GitFixture();
        var (exit, output, _) = RunIn(repo.Path, "lint", GitFixture.ModelPath, "--base", "main");
        Assert.Equal(ExitCodes.BlockingFindings, exit);
        using var doc = JsonDocument.Parse(output);
        Assert.True(doc.RootElement.GetProperty("summary").GetProperty("new").GetInt32() > 0);
        Assert.Equal("main", doc.RootElement.GetProperty("baseRef").GetString());

        // Against itself nothing is new, so the existing errors don't block.
        var (same, text, _) = RunIn(repo.Path, "lint", GitFixture.ModelPath, "--base", "HEAD", "-f", "text");
        Assert.Equal(ExitCodes.Success, same);
        Assert.Contains("0 new since HEAD", text);
    }
}
