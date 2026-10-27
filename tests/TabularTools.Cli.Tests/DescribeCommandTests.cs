using System.Text.Json;

namespace TabularTools.Cli.Tests;

public class DescribeCommandTests
{
    private static (int Exit, string Out, string Err) Run(params string[] args) => RunIn(TestPaths.RepoRoot, terminal: false, args);

    private static (int Exit, string Out, string Err) RunIn(string workingDirectory, bool terminal, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = CliApp.Run(args, new CliContext(stdout, stderr, terminal, workingDirectory));
        return (exit, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void Json_is_the_default_when_output_is_redirected()
    {
        var (exit, output, _) = Run("describe", TestPaths.ExampleSalesPbip, "--detail", "summary");

        Assert.Equal(ExitCodes.Success, exit);
        using var doc = JsonDocument.Parse(output);
        Assert.Equal(6, doc.RootElement.GetProperty("tables").GetArrayLength());
    }

    [Fact]
    public void Text_is_the_default_in_a_terminal_and_the_model_defaults_to_the_current_folder()
    {
        var (exit, output, _) = RunIn(TestPaths.ExampleSalesFolder, terminal: true, "describe", "-d", "summary");

        Assert.Equal(ExitCodes.Success, exit);
        Assert.StartsWith("ExampleSales  (compatibility level 1600, TMDL)", output);
        Assert.Contains("1 calculation group", output);
    }

    [Fact]
    public void Filters_and_markdown_format()
    {
        var (exit, output, _) = Run("describe", TestPaths.ExampleSalesSemanticModel, "--types", "relationship", "--format", "markdown");

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("#### Relationships (4)", output);
        Assert.Contains("bothDirections", output);
        Assert.DoesNotContain("#### Measures", output);
    }

    [Fact]
    public void Returns_every_object_without_paging()
    {
        var (_, output, _) = Run("describe", TestPaths.ExampleSalesPbip);

        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;
        Assert.False(root.GetProperty("truncated").GetBoolean());
        Assert.Equal(root.GetProperty("totalObjects").GetInt32(), root.GetProperty("objects").GetArrayLength());
    }

    [Fact]
    public void Out_writes_to_a_file()
    {
        using var temp = new TempDir();
        var file = temp.Combine("nested", "describe.md");

        var (exit, output, err) = Run("describe", TestPaths.ExampleSalesPbip, "-f", "markdown", "--out", file);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Equal("", output);
        Assert.Contains("Wrote", err);
        Assert.Contains("### Semantic model `ExampleSales`", File.ReadAllText(file));
    }

    [Fact]
    public void Missing_path_exits_with_usage_error()
    {
        var (exit, _, err) = Run("describe", "fixtures/missing");

        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.Contains("LOAD001", err);
    }

    [Fact]
    public void Invalid_tmdl_exits_with_model_error_and_points_at_the_line()
    {
        using var temp = new TempDir(TestPaths.ExampleSalesSemanticModel);
        var file = temp.Combine("definition", "tables", "Store.tmdl");
        File.WriteAllText(file, File.ReadAllText(file).Replace("dataType: string", "dataType: text"));

        var (exit, _, err) = Run("describe", temp.Path);

        Assert.Equal(ExitCodes.ModelError, exit);
        Assert.Contains("TMDL001", err);
        Assert.Contains("Store.tmdl(", err);
    }

    [Theory]
    [InlineData("describe", "--bogus")]
    [InlineData("describe", "--detail", "everything")]
    [InlineData("nonsense")]
    public void Bad_arguments_exit_with_usage_error(params string[] args)
    {
        var (exit, _, err) = Run(args);

        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.Contains("error:", err);
    }
}
