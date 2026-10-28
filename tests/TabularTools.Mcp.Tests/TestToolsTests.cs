using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace TabularTools.Mcp.Tests;

public class TestToolsTests(McpServerFixture server) : IClassFixture<McpServerFixture>
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static JsonElement Structured(CallToolResult result)
    {
        Assert.True(result.IsError != true, result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text);
        return result.StructuredContent!.Value;
    }

    [Fact]
    public async Task Run_measure_tests_replays_the_fixture_recordings()
    {
        var result = await server.Client.CallToolAsync("run_measure_tests", new Dictionary<string, object?>
        {
            ["testFiles"] = new[] { "tests/sales.measuretests.yaml" },
            ["maxDiffs"] = 5,
        }, cancellationToken: Ct);
        var json = Structured(result);
        Assert.Equal(4, json.GetProperty("summary").GetProperty("passed").GetInt32());
        Assert.False(json.GetProperty("blocking").GetBoolean());
        Assert.StartsWith("Measure tests (", Assert.IsType<TextContentBlock>(result.Content[0]).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_measure_tests_errors_are_readable()
    {
        var missing = await server.Client.CallToolAsync("run_measure_tests", new Dictionary<string, object?> { ["testFiles"] = new[] { "nope.measuretests.yaml" } }, cancellationToken: Ct);
        Assert.True(missing.IsError);
        Assert.Contains("Test file not found", Assert.IsType<TextContentBlock>(missing.Content[0]).Text, StringComparison.Ordinal);

        var none = await server.Client.CallToolAsync("run_measure_tests", cancellationToken: Ct);
        Assert.True(none.IsError);
        Assert.Contains("No test files given", Assert.IsType<TextContentBlock>(none.Content[0]).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Generate_previews_without_allow_writes_and_refuses_to_save()
    {
        var preview = Structured(await server.Client.CallToolAsync("generate_measure_tests", new Dictionary<string, object?>
        {
            ["testFile"] = "tests/new.measuretests.yaml",
            ["model"] = "ExampleSales.pbip",
            ["measures"] = new[] { "[Total Sales]" },
            ["groupBy"] = new[] { "'Store'[Region]" },
        }, cancellationToken: Ct));
        Assert.True(preview.GetProperty("write").GetProperty("dryRun").GetBoolean());
        Assert.Contains("- id: total-sales-by-region", preview.GetProperty("yaml").GetString(), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(TestPaths.ExampleSalesFolder, "tests", "new.measuretests.yaml")));

        var save = await server.Client.CallToolAsync("generate_measure_tests", new Dictionary<string, object?>
        {
            ["testFile"] = "tests/new.measuretests.yaml",
            ["model"] = "ExampleSales.pbip",
            ["measures"] = new[] { "[Total Sales]" },
            ["save"] = true,
        }, cancellationToken: Ct);
        Assert.True(save.IsError);
        Assert.Contains("--allow-writes", Assert.IsType<TextContentBlock>(save.Content[0]).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_dax_rejects_commands_and_needs_a_profile()
    {
        var tmsl = await server.Client.CallToolAsync("run_dax", new Dictionary<string, object?> { ["query"] = "{ \"refresh\": { \"type\": \"full\" } }" }, cancellationToken: Ct);
        Assert.True(tmsl.IsError);
        Assert.Contains("XMLA and TMSL commands aren't allowed", Assert.IsType<TextContentBlock>(tmsl.Content[0]).Text, StringComparison.Ordinal);

        var noProfile = await server.Client.CallToolAsync("run_dax", new Dictionary<string, object?> { ["query"] = "EVALUATE {1}" }, cancellationToken: Ct);
        Assert.True(noProfile.IsError);
        Assert.Contains("profile", Assert.IsType<TextContentBlock>(noProfile.Content[0]).Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Schemas_and_test_files_are_resources()
    {
        var schema = await server.Client.ReadResourceAsync("tabulartools://schemas/measure-tests", cancellationToken: Ct);
        var text = Assert.IsType<TextResourceContents>(Assert.Single(schema.Contents)).Text;
        Assert.Contains("\"title\": \"TabularTools measure regression tests\"", text, StringComparison.Ordinal);

        var files = await server.Client.ReadResourceAsync("tabulartools://workspace/test-files", cancellationToken: Ct);
        using var doc = JsonDocument.Parse(Assert.IsType<TextResourceContents>(Assert.Single(files.Contents)).Text);
        var file = Assert.Single(doc.RootElement.EnumerateArray());
        Assert.Equal("tests/sales.measuretests.yaml", file.GetProperty("file").GetString());
        Assert.Equal(3, file.GetProperty("cases").GetArrayLength());
    }
}

public class WritableTestToolsTests(WritableServerFixture server) : IClassFixture<WritableServerFixture>
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task Generate_saves_with_allow_writes_and_reports_the_file()
    {
        var result = await server.Client.CallToolAsync("generate_measure_tests", new Dictionary<string, object?>
        {
            ["testFile"] = "tests/sales.measuretests.yaml",
            ["measures"] = new[] { "[Total Cost]" },
            ["caseId"] = "cost",
            ["save"] = true,
        }, cancellationToken: Ct);
        Assert.NotEqual(true, result.IsError);
        var json = result.StructuredContent!.Value;
        var change = json.GetProperty("write").GetProperty("files")[0];
        Assert.Equal("modified", change.GetProperty("action").GetString());
        Assert.Contains("+  - id: cost", change.GetProperty("unifiedDiff").GetString(), StringComparison.Ordinal);
        Assert.Contains("- id: cost", File.ReadAllText(Path.Combine(server.Root, "tests", "sales.measuretests.yaml")), StringComparison.Ordinal);
        Assert.StartsWith("Updated:", Assert.IsType<TextContentBlock>(result.Content[0]).Text, StringComparison.Ordinal);

        var outside = await server.Client.CallToolAsync("generate_measure_tests", new Dictionary<string, object?>
        {
            ["testFile"] = "../escape.measuretests.yaml",
            ["model"] = "ExampleSales.pbip",
            ["measures"] = new[] { "[Total Cost]" },
            ["save"] = true,
        }, cancellationToken: Ct);
        Assert.True(outside.IsError);
        Assert.Contains("outside the allowed folders", Assert.IsType<TextContentBlock>(outside.Content[0]).Text, StringComparison.Ordinal);
    }
}

public class NoLiveTestToolsTests(NoLiveServerFixture server) : IClassFixture<NoLiveServerFixture>
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task Disable_live_hides_run_dax_and_limits_tests_to_recordings()
    {
        var tools = await server.Client.ListToolsAsync(cancellationToken: Ct);
        Assert.DoesNotContain(tools, t => t.Name == "run_dax");

        var recorded = await server.Client.CallToolAsync("run_measure_tests", new Dictionary<string, object?> { ["testFiles"] = new[] { "tests/sales.measuretests.yaml" } }, cancellationToken: Ct);
        Assert.NotEqual(true, recorded.IsError);

        var live = await server.Client.CallToolAsync("run_measure_tests", new Dictionary<string, object?>
        {
            ["testFiles"] = new[] { "tests/sales.measuretests.yaml" },
            ["profile"] = "dev",
        }, cancellationToken: Ct);
        Assert.True(live.IsError);
        Assert.Contains("--disable-live", Assert.IsType<TextContentBlock>(live.Content[0]).Text, StringComparison.Ordinal);

        var ping = await server.Client.CallToolAsync("ping", cancellationToken: Ct);
        Assert.False(ping.StructuredContent!.Value.GetProperty("liveEnabled").GetBoolean());
    }
}
