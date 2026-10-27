using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace TabularTools.Mcp.Tests;

public class McpServerTests(McpServerFixture server) : IClassFixture<McpServerFixture>
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task Lists_the_m1_tools_all_read_only_with_descriptions_and_output_schemas()
    {
        var tools = await server.Client.ListToolsAsync(cancellationToken: Ct);

        Assert.Equal(["describe_model", "open_model", "ping"], tools.Select(t => t.Name).Order());
        Assert.All(tools, t =>
        {
            var tool = t.ProtocolTool;
            Assert.False(string.IsNullOrWhiteSpace(tool.Description));
            Assert.True(tool.Annotations?.ReadOnlyHint);
            Assert.False(tool.Annotations?.DestructiveHint);
            Assert.False(tool.Annotations?.OpenWorldHint);
            Assert.NotNull(tool.OutputSchema);
        });
    }

    [Fact]
    public async Task Describe_model_offers_detail_as_a_string_enum()
    {
        var tools = await server.Client.ListToolsAsync(cancellationToken: Ct);
        var schema = tools.Single(t => t.Name == "describe_model").ProtocolTool.InputSchema;

        var detail = schema.GetProperty("properties").GetProperty("detail");
        Assert.Equal(["summary", "objects", "full"], detail.GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Ping_reports_versions_and_read_only_mode()
    {
        var result = await server.Client.CallToolAsync("ping", cancellationToken: Ct);

        var json = Structured(result);
        Assert.Equal("TabularTools", json.GetProperty("name").GetString());
        Assert.False(json.GetProperty("writesEnabled").GetBoolean());
        Assert.Equal(TestPaths.ExampleSalesFolder, json.GetProperty("roots")[0].GetString());
    }

    [Fact]
    public async Task Open_model_without_a_path_finds_the_single_model_under_the_root()
    {
        var result = await server.Client.CallToolAsync("open_model", cancellationToken: Ct);

        var json = Structured(result);
        Assert.StartsWith("m-", json.GetProperty("modelId").GetString());
        var model = json.GetProperty("model");
        Assert.Equal("ExampleSales", model.GetProperty("name").GetString());
        Assert.Equal(11, model.GetProperty("counts").GetProperty("measures").GetInt32());
        Assert.Contains("Opened 'ExampleSales'", Text(result, 0));
    }

    [Fact]
    public async Task Describe_model_accepts_a_handle_and_returns_expressions_at_full_detail()
    {
        var open = Structured(await server.Client.CallToolAsync("open_model", new Dictionary<string, object?> { ["path"] = "ExampleSales.pbip" }, cancellationToken: Ct));
        var handle = open.GetProperty("modelId").GetString();

        var result = await server.Client.CallToolAsync("describe_model", new Dictionary<string, object?>
        {
            ["model"] = handle,
            ["detail"] = "full",
            ["objectTypes"] = new[] { "measure" },
            ["namePattern"] = "Total*",
        }, cancellationToken: Ct);

        var objects = Structured(result).GetProperty("objects").EnumerateArray().ToList();
        Assert.Equal(["Total Sales", "Total Quantity", "Total Cost"], objects.Select(o => o.GetProperty("name").GetString()));
        Assert.Equal("SUM(Sales[Amount])", objects[0].GetProperty("expression").GetString());
    }

    [Fact]
    public async Task Describe_model_pages_with_a_cursor()
    {
        var first = Structured(await server.Client.CallToolAsync("describe_model", new Dictionary<string, object?> { ["limit"] = 5 }, cancellationToken: Ct));
        Assert.True(first.GetProperty("truncated").GetBoolean());

        var second = Structured(await server.Client.CallToolAsync("describe_model", new Dictionary<string, object?>
        {
            ["limit"] = 5,
            ["cursor"] = first.GetProperty("nextCursor").GetString(),
        }, cancellationToken: Ct));

        var firstKeys = first.GetProperty("objects").EnumerateArray().Select(o => o.GetProperty("key").GetString());
        var secondKeys = second.GetProperty("objects").EnumerateArray().Select(o => o.GetProperty("key").GetString());
        Assert.Empty(firstKeys.Intersect(secondKeys));
    }

    [Fact]
    public async Task Bad_path_is_a_readable_tool_error()
    {
        var result = await server.Client.CallToolAsync("open_model", new Dictionary<string, object?> { ["path"] = "missing" }, cancellationToken: Ct);

        Assert.True(result.IsError);
        Assert.Contains("LOAD001", Text(result, 0));
    }

    [Theory]
    [InlineData("ping", null)]
    [InlineData("open_model", null)]
    [InlineData("describe_model", "summary")]
    [InlineData("describe_model", "objects")]
    [InlineData("describe_model", "full")]
    public async Task Structured_content_has_every_property_its_output_schema_requires(string tool, string? detail)
    {
        var tools = await server.Client.ListToolsAsync(cancellationToken: Ct);
        var schema = tools.Single(t => t.Name == tool).ProtocolTool.OutputSchema!.Value;
        var args = detail is null ? null : new Dictionary<string, object?> { ["detail"] = detail };

        var result = await server.Client.CallToolAsync(tool, args, cancellationToken: Ct);

        var problems = new List<string>();
        CheckRequired(schema, Structured(result), "$", problems);
        Assert.Empty(problems);
    }

    private static JsonElement Structured(CallToolResult result)
    {
        Assert.NotEqual(true, result.IsError);
        Assert.NotNull(result.StructuredContent);
        return result.StructuredContent!.Value;
    }

    private static string Text(CallToolResult result, int index) => Assert.IsType<TextContentBlock>(result.Content[index]).Text;

    /// <summary>Minimal JSON Schema check: every "required" property is present, recursively.</summary>
    private static void CheckRequired(JsonElement schema, JsonElement value, string path, List<string> problems)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("required", out var required))
                foreach (var name in required.EnumerateArray().Select(r => r.GetString()!))
                    if (!value.TryGetProperty(name, out _)) problems.Add($"{path}.{name} is required but missing");

            if (schema.TryGetProperty("properties", out var properties))
                foreach (var prop in value.EnumerateObject())
                    if (properties.TryGetProperty(prop.Name, out var propSchema))
                        CheckRequired(propSchema, prop.Value, $"{path}.{prop.Name}", problems);
        }
        else if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var items))
        {
            var i = 0;
            foreach (var item in value.EnumerateArray()) CheckRequired(items, item, $"{path}[{i++}]", problems);
        }
    }
}
