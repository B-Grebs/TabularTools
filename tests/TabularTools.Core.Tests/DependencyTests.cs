using TabularTools.Core.Dax;
using TabularTools.Core.Loading;

namespace TabularTools.Core.Tests;

public class DependencyTests
{
    private static readonly Lazy<LoadedModel> Fixture = new(() => ModelLoader.Load(TestPaths.ExampleSalesPbip));

    private static DependencyGraph Graph => Fixture.Value.Dependencies;

    [Fact]
    public void Qualified_and_unqualified_references_are_resolved()
    {
        var margin = Graph.DependenciesOf("table:Sales/measure:Margin");
        Assert.All(margin, r => Assert.True(r.IsResolved));
        Assert.Equal(["Total Cost", "Total Sales"], margin.Select(r => r.Name).Distinct().Order());
        Assert.All(margin, r => Assert.Equal(ReferenceKind.Measure, r.Kind));
        Assert.All(margin, r => Assert.False(r.FullyQualified));

        var totalCost = Graph.DependenciesOf("table:Sales/measure:Total Cost");
        Assert.Contains(totalCost, r => r is { Kind: ReferenceKind.Table, Target: "table:Sales" });
        Assert.Contains(totalCost, r => r is { Kind: ReferenceKind.Column, Target: "table:Sales/column:Unit Cost", FullyQualified: true });
    }

    [Fact]
    public void Unqualified_column_inside_an_iterator_resolves_to_the_iterated_table()
    {
        var averagePrice = Graph.DependenciesOf("table:Sales/measure:Average Price");

        var price = Assert.Single(averagePrice, r => r.Kind == ReferenceKind.Column);
        Assert.Equal("table:Sales/column:Unit Price", price.Target);
        Assert.False(price.FullyQualified);
    }

    [Fact]
    public void Calculated_columns_rls_filters_and_calculation_items_are_analysed()
    {
        Assert.Contains(Graph.DependenciesOf("table:Sales/column:Product Category"), r => r.Target == "table:Product/column:Category");

        var rls = Assert.Single(Graph.DependenciesOf("role:West Region/tablePermission:Store"));
        Assert.Equal("table:Store/column:Region", rls.Target);
        Assert.Equal(ExpressionPart.Filter, rls.Part);

        Assert.Contains(Graph.DependenciesOf("table:Time Intelligence/calculationItem:YTD"), r => r.Target == "table:Date/column:Date");
    }

    [Fact]
    public void Reverse_index_and_structural_uses()
    {
        var users = Graph.ReferencesTo("table:Sales/measure:Total Sales").Select(r => r.Owner).Distinct().Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["table:Sales/measure:Margin", "table:Sales/measure:Margin %", "table:Sales/measure:Sales PY", "table:Sales/measure:Sales YTD", "table:Sales/measure:Sales YoY %"], users);

        Assert.Contains(Graph.StructuralUsesOf("table:Date/column:Month Number"), s => s.Kind == StructuralUseKind.SortBy);
        Assert.Contains(Graph.StructuralUsesOf("table:Date/column:Year"), s => s.Kind == StructuralUseKind.Hierarchy);
        Assert.Contains(Graph.StructuralUsesOf("table:Sales/column:DateKey"), s => s.Kind == StructuralUseKind.Relationship);
    }

    [Fact]
    public void The_fixture_has_no_unresolved_references()
    {
        Assert.Empty(Graph.Unresolved);
    }

    [Fact]
    public void Broken_and_local_references_are_told_apart()
    {
        using var temp = new TempDir(TestPaths.ExampleSalesSemanticModel);
        var sales = temp.Combine("definition", "tables", "Sales.tmdl");
        File.WriteAllText(sales, File.ReadAllText(sales).Replace(
            "measure 'Customer Count' = DISTINCTCOUNT(Sales[CustomerKey])",
            "measure 'Customer Count' = VAR t = ADDCOLUMNS(Sales, \"Doubled\", Sales[Quantity] * 2) RETURN SUMX(t, [Doubled]) + SUM(Sales[Missing]) + [No Such Measure] + SUMX(t, t[Doubled])"));

        var graph = ModelLoader.Load(temp.Path).Dependencies;

        var unresolved = graph.Unresolved.Select(r => r.Text).Order().ToList();
        Assert.Equal(["[No Such Measure]", "Sales[Missing]"], unresolved);
    }

    [Fact]
    public void Query_walks_upstream_and_downstream_to_the_requested_depth()
    {
        var model = Fixture.Value;

        var direct = DependencyQuery.Query(model.Snapshot, model.Dependencies, "[Total Sales]", DependencyDirection.Downstream, depth: 1);
        Assert.DoesNotContain(direct.Downstream, e => e.From.Name == "Sales YoY %" && e.Depth > 1);

        var all = DependencyQuery.Query(model.Snapshot, model.Dependencies, "Sales[Amount]", DependencyDirection.Downstream, depth: 0);
        var owners = all.Downstream.Select(e => e.From.Name).ToHashSet();
        Assert.Contains("Total Sales", owners);
        Assert.Contains("Sales YoY %", owners); // Amount → Total Sales → Sales PY → Sales YoY %
        Assert.Equal(3, all.Downstream.Single(e => e.From.Name == "Sales YoY %" && e.To.Name == "Sales PY").Depth);

        var up = DependencyQuery.Query(model.Snapshot, model.Dependencies, "'Sales'[Margin %]", DependencyDirection.Upstream, depth: 0);
        Assert.Contains(up.Upstream, e => e.To.Key == "table:Sales/column:Amount");
    }

    [Fact]
    public void Query_includes_structural_uses_downstream()
    {
        var model = Fixture.Value;

        var result = DependencyQuery.Query(model.Snapshot, model.Dependencies, "'Date'[Month Number]", DependencyDirection.Downstream);

        Assert.Contains(result.Downstream, e => e.Via == "sortBy" && e.From.Key == "table:Date/column:Month Name");
    }

    [Theory]
    [InlineData("Sales[Amount]", "table:Sales/column:Amount")]
    [InlineData("'Sales'[amount]", "table:Sales/column:Amount")]
    [InlineData("[Total Sales]", "table:Sales/measure:Total Sales")]
    [InlineData("Sales[Total Sales]", "table:Sales/measure:Total Sales")]
    [InlineData("[Segment]", "table:Customer/column:Segment")]
    [InlineData("'Date'", "table:Date")]
    [InlineData("Margin", "table:Sales/measure:Margin")]
    [InlineData("table:Store/column:Region", "table:Store/column:Region")]
    public void Object_specs_resolve(string spec, string key)
    {
        var model = Fixture.Value;
        Assert.Equal(key, DependencyQuery.ResolveObject(model.Snapshot, model.Dependencies, spec));
    }

    [Theory]
    [InlineData("[DateKey]", "ambiguous")]
    [InlineData("Nope[Amount]", "No table")]
    [InlineData("[Nope]", "No measure or column")]
    public void Bad_object_specs_explain_the_problem(string spec, string message)
    {
        var model = Fixture.Value;
        var ex = Assert.Throws<ArgumentException>(() => DependencyQuery.ResolveObject(model.Snapshot, model.Dependencies, spec));
        Assert.Contains(message, ex.Message);
    }
}
