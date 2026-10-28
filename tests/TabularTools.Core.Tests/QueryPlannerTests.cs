using TabularTools.Core.Diff;
using TabularTools.Core.Loading;
using TabularTools.Core.Testing;

namespace TabularTools.Core.Tests;

public sealed class QueryPlannerTests
{
    private static ResolvedCase Case(TestCase c, MeasureTestFile? file = null)
    {
        var model = ModelLoader.Load(TestPaths.ExampleSalesSemanticModel);
        return CaseResolver.Resolve(c, file ?? new MeasureTestFile { Model = "." }, model.Snapshot, model.Dependencies, null);
    }

    [Fact]
    public void Writes_summarizecolumns_with_overrides_filters_and_order()
    {
        var c = Case(new TestCase { Id = "a", Measures = ["[Total Sales]", "Sales[Margin %]"], GroupBy = ["'Date'[Year]", "Store[Region]"], Filters = ["'Date'[Year] = 2023"] });
        var defs = new SideDefinitions([new QueryDefinition("k", "Sales", "Total Sales", "SUMX (\n    Sales,\n    Sales[Quantity] * Sales[Unit Price]\n)\n")], []);

        var q = QueryPlanner.Values(c, defs, m => m.Name, "a/head");

        Assert.Equal("""
            DEFINE
            	MEASURE 'Sales'[Total Sales] =
            		SUMX (
            		    Sales,
            		    Sales[Quantity] * Sales[Unit Price]
            		)
            EVALUATE
            CALCULATETABLE (
            	SUMMARIZECOLUMNS (
            		'Date'[Year],
            		'Store'[Region],
            		"Total Sales", [Total Sales],
            		"Margin %", [Margin %]
            	),
            	'Date'[Year] = 2023
            )
            ORDER BY 'Date'[Year], 'Store'[Region]

            """.Replace("\r\n", "\n", StringComparison.Ordinal), q.Text);
        Assert.Equal(["Date[Year]", "Store[Region]", "Total Sales", "Margin %"], q.Columns.Select(x => x.Label));
        Assert.Equal([true, true, false, false], q.Columns.Select(x => x.IsGroup));
        Assert.Equal("a/head", q.Label);
    }

    [Fact]
    public void Without_filters_or_columns_the_query_is_a_bare_summarizecolumns()
    {
        var q = QueryPlanner.Values(Case(new TestCase { Id = "a", Measures = ["[Total Sales]"] }), SideDefinitions.Empty, m => m.Name, "a");
        Assert.Equal("EVALUATE\nSUMMARIZECOLUMNS (\n\t\"Total Sales\", [Total Sales]\n)\n", q.Text);
    }

    [Fact]
    public void Base_side_uses_base_names_and_leaves_out_new_measures()
    {
        var c = Case(new TestCase { Id = "a", Measures = ["[Total Sales]", "[Total Cost]"] });
        var q = QueryPlanner.Values(c, SideDefinitions.Empty, m => m.Name == "Total Cost" ? null : "Old Name", "a/base");
        Assert.Contains("\"Total Sales\", [Old Name]", q.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Total Cost", q.Text, StringComparison.Ordinal);
        Assert.Equal(["Total Sales"], q.Columns.Select(x => x.Label));
    }

    [Fact]
    public void Invariant_query_returns_only_violating_rows_that_have_values()
    {
        var c = Case(new TestCase { Id = "a", Measures = ["[Total Sales]", "[Margin %]"], GroupBy = ["Store[Region]"] });
        var q = QueryPlanner.Invariant(c, "[Margin %] <= 1", SideDefinitions.Empty, "a/invariant-1");
        Assert.Contains("\"__violation\", IF ( NOT ( ISBLANK ( [Total Sales] ) && ISBLANK ( [Margin %] ) ) && NOT ( [Margin %] <= 1 ), 1 )", q.Text, StringComparison.Ordinal);
        Assert.Equal(["Store[Region]", "__violation"], q.Columns.Select(x => x.Label));
    }

    [Fact]
    public void Names_with_quotes_and_brackets_are_escaped()
    {
        Assert.Equal("'O''Brien'[A]]B]", QueryPlanner.Column("O'Brien", "A]B"));
        Assert.Equal("\"Say \"\"hi\"\"\"", QueryPlanner.Label("Say \"hi\""));
    }

    [Fact]
    public void Case_resolution_reports_unknown_and_misplaced_objects()
    {
        var c = Case(new TestCase
        {
            Id = "a",
            Measures = ["[Totl Sales]", "Sales[Amount]", "[Total Sales]", "[Total Sales]"],
            GroupBy = ["[Total Cost]", "'Date'[Nope]"],
            Filters = ["Store[Regoin] = \"West\""],
            AsRole = "Nobody",
            Assertions = [new TestAssertion { Type = "invariant", Expr = "[Margn %] > 0" }],
        });
        var problems = string.Join("\n", c.Problems);
        Assert.Contains("'[Totl Sales]'", problems, StringComparison.Ordinal);
        Assert.Contains("'Sales[Amount]' is a column, not a measure", problems, StringComparison.Ordinal);
        Assert.Contains("listed twice", problems, StringComparison.Ordinal);
        Assert.Contains("'[Total Cost]' is a measure, not a column", problems, StringComparison.Ordinal);
        Assert.Contains("[Nope]", problems, StringComparison.Ordinal);
        Assert.Contains("table 'Store' has no column or measure [Regoin]", problems, StringComparison.Ordinal);
        Assert.Contains("[Margn %]", problems, StringComparison.Ordinal);
        Assert.Contains("role 'Nobody'", problems, StringComparison.Ordinal);
    }

    [Fact]
    public void Tolerance_layers_case_then_file_then_config_then_default()
    {
        var t = CaseResolver.MergeTolerance(new Config.ToleranceSettings { Absolute = 1 }, new Config.ToleranceSettings { Absolute = 2, Relative = 0.5 }, null);
        Assert.Equal(new Queries.Tolerance(1, 0.5), t);
        Assert.Equal(Queries.Tolerance.Default, CaseResolver.MergeTolerance(null, null, null));
    }
}
