using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace TabularTools.Mcp.Tests;

public class DiffToolsTests(McpServerFixture server) : IClassFixture<McpServerFixture>
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task Diff_model_is_read_only_and_local()
    {
        var tools = await server.Client.ListToolsAsync(cancellationToken: Ct);
        var annotations = tools.Single(t => t.Name == "diff_model").ProtocolTool.Annotations!;
        Assert.True(annotations.ReadOnlyHint);
        Assert.False(annotations.OpenWorldHint);
        Assert.False(annotations.DestructiveHint);
    }

    [Fact]
    public async Task Diff_model_compares_two_folders_and_returns_markdown_on_request()
    {
        var result = await server.Client.CallToolAsync("diff_model", new Dictionary<string, object?>
        {
            ["base"] = Path.Combine(TestPaths.Fixtures, "diff", "ExampleSales-v1", "ExampleSales.SemanticModel"),
            ["head"] = Path.Combine(TestPaths.Fixtures, "diff", "ExampleSales-v2", "ExampleSales.SemanticModel"),
            ["markdown"] = true,
            ["minSeverity"] = "high",
        }, cancellationToken: Ct);

        Assert.NotEqual(true, result.IsError);
        var model = result.StructuredContent!.Value.GetProperty("models")[0];
        Assert.Equal(9, model.GetProperty("changes").GetArrayLength());
        Assert.Contains("Semantic model review", model.GetProperty("markdown").GetString());
        Assert.Contains("5 breaking", Assert.IsType<TextContentBlock>(result.Content[0]).Text);
    }

    [Fact]
    public async Task Diff_model_reads_git_refs_and_lint_model_marks_new_violations()
    {
        using var repo = new GitFixture();
        var diff = await server.Client.CallToolAsync("diff_model", new Dictionary<string, object?>
        {
            ["repo"] = repo.Path,
            ["base"] = "main",
            ["limit"] = 2,
        }, cancellationToken: Ct);
        Assert.NotEqual(true, diff.IsError);
        var json = diff.StructuredContent!.Value;
        Assert.Equal(repo.MainCommit, json.GetProperty("base").GetProperty("commit").GetString());
        var model = json.GetProperty("models")[0];
        Assert.Equal(2, model.GetProperty("changes").GetArrayLength());
        Assert.Equal(13, model.GetProperty("summary").GetProperty("total").GetInt32());

        var lint = await server.Client.CallToolAsync("lint_model", new Dictionary<string, object?>
        {
            ["model"] = repo.Combine(GitFixture.ModelPath),
            ["baseRef"] = "main",
        }, cancellationToken: Ct);
        Assert.NotEqual(true, lint.IsError);
        var summary = lint.StructuredContent!.Value.GetProperty("summary");
        Assert.True(summary.GetProperty("new").GetInt32() > 0);
        Assert.True(summary.GetProperty("existing").GetInt32() > 0);
    }

    [Fact]
    public async Task Git_errors_are_tool_errors()
    {
        using var repo = new GitFixture();
        var result = await server.Client.CallToolAsync("diff_model", new Dictionary<string, object?> { ["repo"] = repo.Path, ["base"] = "nope" }, cancellationToken: Ct);
        Assert.True(result.IsError);
        Assert.Contains("Unknown git ref 'nope'", Assert.IsType<TextContentBlock>(result.Content[0]).Text);
    }

    [Fact]
    public async Task Change_kinds_are_available_as_a_resource()
    {
        var resource = await server.Client.ReadResourceAsync("tabulartools://diff/change-kinds", cancellationToken: Ct);
        var text = Assert.IsType<TextResourceContents>(resource.Contents[0]).Text;
        using var doc = JsonDocument.Parse(text);
        Assert.Contains(doc.RootElement.EnumerateArray(), k => k.GetProperty("kind").GetString() == "role.rlsFilterChanged"
                                                              && k.GetProperty("severity").GetString() == "breaking");
    }
}
