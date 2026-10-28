using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace TabularTools.Mcp.Tests;

public class LintToolsTests(McpServerFixture server) : IClassFixture<McpServerFixture>
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task Lint_model_reports_violations_with_rule_details_and_a_summary_line()
    {
        var result = await server.Client.CallToolAsync("lint_model", cancellationToken: Ct);

        Assert.NotEqual(true, result.IsError);
        var json = result.StructuredContent!.Value;
        Assert.True(json.GetProperty("blocking").GetBoolean());
        var violations = json.GetProperty("violations").EnumerateArray().ToList();
        Assert.Contains(violations, v => v.GetProperty("ruleId").GetString() == "AVOID_FLOATING_POINT_DATA_TYPES"
                                         && v.GetProperty("object").GetProperty("key").GetString() == "table:Sales/column:Amount");
        Assert.True(json.GetProperty("rules").TryGetProperty("TT004", out var tt004));
        Assert.Equal("info", tt004.GetProperty("severity").GetString());
        Assert.Contains("evaluated", Assert.IsType<TextContentBlock>(result.Content[0]).Text);
    }

    [Fact]
    public async Task Lint_model_filters_and_limits()
    {
        var result = await server.Client.CallToolAsync("lint_model", new Dictionary<string, object?>
        {
            ["rulesets"] = new[] { "microsoft-bpa" },
            ["minSeverity"] = "warning",
            ["limit"] = 3,
        }, cancellationToken: Ct);

        var json = result.StructuredContent!.Value;
        Assert.Equal(3, json.GetProperty("violations").GetArrayLength());
        Assert.True(json.GetProperty("truncated").GetBoolean());
        Assert.True(json.GetProperty("summary").GetProperty("total").GetInt32() > 3);
        Assert.All(json.GetProperty("violations").EnumerateArray(), v => Assert.NotEqual("info", v.GetProperty("severity").GetString()));
    }

    [Fact]
    public async Task Get_dependencies_walks_downstream()
    {
        var result = await server.Client.CallToolAsync("get_dependencies", new Dictionary<string, object?>
        {
            ["object"] = "Sales[Amount]",
            ["direction"] = "downstream",
            ["depth"] = 0,
        }, cancellationToken: Ct);

        var json = result.StructuredContent!.Value;
        Assert.Equal("table:Sales/column:Amount", json.GetProperty("object").GetProperty("key").GetString());
        var users = json.GetProperty("downstream").EnumerateArray().Select(e => e.GetProperty("from").GetProperty("name").GetString()).ToHashSet();
        Assert.Contains("Total Sales", users);
        Assert.Contains("Sales YoY %", users);
    }

    [Fact]
    public async Task Get_dependencies_explains_an_ambiguous_reference()
    {
        var result = await server.Client.CallToolAsync("get_dependencies", new Dictionary<string, object?> { ["object"] = "[DateKey]" }, cancellationToken: Ct);

        Assert.True(result.IsError);
        Assert.Contains("ambiguous", Assert.IsType<TextContentBlock>(result.Content[0]).Text);
    }

    [Fact]
    public async Task Rulesets_are_resources()
    {
        var templates = await server.Client.ListResourceTemplatesAsync(cancellationToken: Ct);
        Assert.Contains(templates, t => t.UriTemplate == "tabulartools://rulesets/{id}");

        var list = await server.Client.ReadResourceAsync("tabulartools://rulesets", cancellationToken: Ct);
        using var listDoc = JsonDocument.Parse(Assert.IsType<TextResourceContents>(list.Contents[0]).Text);
        Assert.Equal(["builtin", "microsoft-bpa"], listDoc.RootElement.EnumerateArray().Select(r => r.GetProperty("id").GetString()));

        var bpa = await server.Client.ReadResourceAsync("tabulartools://rulesets/microsoft-bpa", cancellationToken: Ct);
        using var bpaDoc = JsonDocument.Parse(Assert.IsType<TextResourceContents>(bpa.Contents[0]).Text);
        Assert.Equal(71, bpaDoc.RootElement.GetProperty("rules").GetArrayLength());
    }
}
