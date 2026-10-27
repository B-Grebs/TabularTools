using System.Text.Json;
using TabularTools.Core.Describe;
using TabularTools.Core.Loading;
using TabularTools.Core.Rendering;

namespace TabularTools.Core.Tests;

public class ModelDescriberTests
{
    private static readonly Lazy<LoadedModel> Fixture = new(() => ModelLoader.Load(TestPaths.ExampleSalesPbip));

    [Fact]
    public void Summary_lists_tables_with_counts_and_no_objects()
    {
        var result = ModelDescriber.Describe(Fixture.Value, new DescribeRequest { Detail = DetailLevel.Summary });

        Assert.Null(result.Objects);
        Assert.Equal(6, result.Tables!.Count);
        var sales = result.Tables.Single(t => t.Name == "Sales");
        Assert.Equal((11, 10, 1), (sales.Columns, sales.Measures, sales.Partitions));
        Assert.Equal("calculationGroup", result.Tables.Single(t => t.Name == "Time Intelligence").Kind);
        Assert.Equal(11, result.Model.Counts.Measures);
        Assert.Equal(1, result.Model.Counts.CalculationGroups);
    }

    [Fact]
    public void Objects_detail_omits_expressions_and_full_includes_them()
    {
        var objects = ModelDescriber.Describe(Fixture.Value, new DescribeRequest { ObjectTypes = [ObjectType.Measure] });
        var full = ModelDescriber.Describe(Fixture.Value, new DescribeRequest { ObjectTypes = [ObjectType.Measure], Detail = DetailLevel.Full });

        Assert.All(objects.Objects!, o => Assert.Null(o.Expression));
        Assert.Equal("SUM(Sales[Amount])", full.Objects!.Single(o => o.Name == "Total Sales").Expression);
        Assert.Equal(11, full.TotalObjects);
    }

    [Fact]
    public void Filters_by_table_type_name_and_visibility()
    {
        var result = ModelDescriber.Describe(Fixture.Value, new DescribeRequest
        {
            Tables = ["sales"],
            ObjectTypes = [ObjectType.Column],
            NamePattern = "*Key",
            IncludeHidden = false,
        });

        var only = Assert.Single(result.Objects!);
        Assert.Equal("table:Sales/column:CustomerKey", only.Key);
    }

    [Fact]
    public void Relationships_roles_and_parameters_carry_their_properties()
    {
        var result = ModelDescriber.Describe(Fixture.Value, new DescribeRequest
        {
            ObjectTypes = [ObjectType.Relationship, ObjectType.Role, ObjectType.Expression],
            Detail = DetailLevel.Full,
        });

        var relationship = result.Objects!.Single(o => o.Key == "relationship:Sales[CustomerKey]->Customer[CustomerKey]");
        Assert.Equal("bothDirections", relationship.Properties!["crossFilteringBehavior"]);
        Assert.Equal("many:one", relationship.Properties["cardinality"]);

        var role = result.Objects!.Single(o => o.Type == "role");
        var permission = Assert.Single((IEnumerable<Dictionary<string, object?>>)role.Properties!["tablePermissions"]!);
        Assert.Equal("[Region] = \"West\"", permission["filterExpression"]);

        Assert.All(result.Objects!.Where(o => o.Type == "expression"), e => Assert.Equal(true, e.Properties!["isParameter"]));
    }

    [Fact]
    public void Paging_returns_every_object_exactly_once()
    {
        var all = ModelDescriber.Describe(Fixture.Value, new DescribeRequest { Limit = ModelDescriber.MaxLimit });
        var seen = new List<string>();
        string? cursor = null;
        do
        {
            var page = ModelDescriber.Describe(Fixture.Value, new DescribeRequest { Limit = 7, Cursor = cursor });
            seen.AddRange(page.Objects!.Select(o => o.Key));
            Assert.True(page.Objects!.Count <= 7);
            cursor = page.NextCursor;
            Assert.Equal(cursor is not null, page.Truncated);
        }
        while (cursor is not null);

        Assert.Equal(all.Objects!.Select(o => o.Key), seen);
        Assert.Equal(seen.Count, seen.Distinct().Count());
    }

    [Fact]
    public void Invalid_cursor_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => ModelDescriber.Describe(Fixture.Value, new DescribeRequest { Cursor = "not-a-cursor" }));
    }

    [Fact]
    public void Json_output_uses_camel_case_and_string_enums()
    {
        var result = ModelDescriber.Describe(Fixture.Value, new DescribeRequest { Detail = DetailLevel.Summary });

        using var doc = JsonDocument.Parse(OutputJson.Serialize(result));
        var root = doc.RootElement;
        Assert.Equal("summary", root.GetProperty("detail").GetString());
        Assert.Equal("pbip", root.GetProperty("model").GetProperty("sourceKind").GetString());
        Assert.Equal("tmdl", root.GetProperty("model").GetProperty("format").GetString());
        Assert.False(root.TryGetProperty("objects", out _));
    }

    [Fact]
    public void Markdown_and_text_renderings_mention_key_objects()
    {
        var result = ModelDescriber.Describe(Fixture.Value, new DescribeRequest { Detail = DetailLevel.Full });

        var markdown = DescribeRenderer.Markdown(result);
        var text = DescribeRenderer.Text(result);

        Assert.Contains("### Semantic model `ExampleSales`", markdown);
        Assert.Contains("Sales[Total Sales]", markdown);
        Assert.Contains("```dax", markdown);
        Assert.Contains("Sales[CustomerKey] → Customer[CustomerKey]", text);
        Assert.Contains("SUM(Sales[Amount])", text);
    }
}
