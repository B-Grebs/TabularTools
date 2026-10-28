using System.Text.Json;

namespace TabularTools.Cli.Tests;

public class LintCommandTests
{
    private static (int Exit, string Out, string Err) Run(params string[] args) => RunIn(TestPaths.RepoRoot, args);

    private static (int Exit, string Out, string Err) RunIn(string workingDirectory, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = CliApp.Run(args, new CliContext(stdout, stderr, false, workingDirectory));
        return (exit, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void Lint_exits_1_when_errors_are_found_and_0_when_fail_on_is_none()
    {
        var (blocking, output, _) = Run("lint", TestPaths.ExampleSalesPbip);
        Assert.Equal(ExitCodes.BlockingFindings, blocking);
        using var doc = JsonDocument.Parse(output);
        Assert.True(doc.RootElement.GetProperty("summary").GetProperty("error").GetInt32() > 0);

        var (relaxed, _, _) = Run("lint", TestPaths.ExampleSalesPbip, "--fail-on", "none");
        Assert.Equal(ExitCodes.Success, relaxed);
    }

    [Fact]
    public void Lint_markdown_groups_by_severity()
    {
        var (_, output, _) = Run("lint", TestPaths.ExampleSalesPbip, "-f", "markdown", "--fail-on", "none");

        Assert.Contains("### Lint — `ExampleSales`", output);
        Assert.Contains("#### 🔴 Errors", output);
        Assert.Contains("<summary>🟠 Warnings", output);
        Assert.Contains("`AVOID_FLOATING_POINT_DATA_TYPES`", output);
    }

    [Fact]
    public void Lint_uses_the_nearest_config_file()
    {
        using var temp = new TempDir(TestPaths.ExampleSalesFolder);
        File.WriteAllText(temp.Combine("tabulartools.yaml"), """
            lint:
              rulesets: [builtin]
              failOn: info
            """);

        var (exit, output, _) = Run("lint", temp.Path);

        Assert.Equal(ExitCodes.BlockingFindings, exit); // TT004 is info and failOn is info
        using var doc = JsonDocument.Parse(output);
        Assert.Equal("builtin", Assert.Single(doc.RootElement.GetProperty("rulesets").EnumerateArray()).GetProperty("id").GetString());
        Assert.EndsWith("tabulartools.yaml", doc.RootElement.GetProperty("configPath").GetString());
    }

    [Fact]
    public void Invalid_config_is_a_usage_error()
    {
        using var temp = new TempDir(TestPaths.ExampleSalesFolder);
        File.WriteAllText(temp.Combine("tabulartools.yaml"), "lint:\n  failOn: sometimes\n");

        var (exit, _, err) = Run("lint", temp.Path);

        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.Contains("failOn", err);
    }

    [Fact]
    public void Deps_prints_users_of_a_column()
    {
        var (exit, output, _) = Run("deps", "Sales[Amount]", "--model", TestPaths.ExampleSalesPbip, "--direction", "downstream", "-f", "text");

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("Used by", output);
        Assert.Contains("Sales[Total Sales]", output);
    }

    [Fact]
    public void Deps_with_an_unknown_object_is_a_usage_error()
    {
        var (exit, _, err) = Run("deps", "[Nope]", "--model", TestPaths.ExampleSalesPbip);

        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.Contains("No measure or column", err);
    }

    [Fact]
    public void Rules_check_passes_the_vendored_file_and_fails_a_bad_rule()
    {
        var (ok, output, _) = Run("rules", "check", "rules/microsoft-bpa/BPARules.json", "-f", "text");
        Assert.Equal(ExitCodes.Success, ok);
        Assert.Contains("71 of 71", output);

        using var temp = new TempDir();
        File.WriteAllText(temp.Combine("bad.json"), """[ { "ID": "BAD", "Name": "x", "Scope": "Measure", "Expression": "Nonsense = 1" } ]""");
        var (failed, badOutput, _) = Run("rules", "check", temp.Combine("bad.json"), "-f", "text");
        Assert.Equal(ExitCodes.BlockingFindings, failed);
        Assert.Contains("FAILED  BAD", badOutput);
    }

    [Fact]
    public void Rules_list_shows_both_default_rulesets()
    {
        using var temp = new TempDir();
        var (exit, output, _) = RunIn(temp.Path, "rules", "list", "-f", "text");

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("builtin (builtin, 5 rules)", output);
        Assert.Contains("microsoft-bpa (embedded, 71 rules)", output);
    }
}
