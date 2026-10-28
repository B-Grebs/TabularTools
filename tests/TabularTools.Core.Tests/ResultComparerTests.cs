using TabularTools.Core.Loading;
using TabularTools.Core.Queries;
using TabularTools.Core.Testing;

namespace TabularTools.Core.Tests;

public sealed class ResultComparerTests
{
    private static readonly ResolvedCase Case = Resolve();

    private static ResolvedCase Resolve()
    {
        var model = ModelLoader.Load(TestPaths.ExampleSalesSemanticModel);
        return CaseResolver.Resolve(new TestCase { Id = "a", Measures = ["[Total Sales]", "[Margin %]"], GroupBy = ["Store[Region]"], Tolerance = new Config.ToleranceSettings { Absolute = 0.01, Relative = 0 } },
            new MeasureTestFile { Model = "." }, model.Snapshot, model.Dependencies, null);
    }

    private static readonly PlannedQuery Query = QueryPlanner.Values(Case, SideDefinitions.Empty, m => m.Name, "a");

    private static ResultComparer.IndexedResult Rows(params object?[][] rows) => ResultComparer.Index(Query, new DaxResult
    {
        Columns = [new("Store[Region]", "string"), new("[Total Sales]", "decimal"), new("[Margin %]", "double")],
        Rows = rows.Select(r => (IReadOnlyList<object?>)r).ToList(),
        Target = new DaxTarget("x", "y"),
    });

    [Fact]
    public void Matches_rows_by_key_not_position_and_within_tolerance()
    {
        var @base = Rows(["East", 100m, 0.25], ["West", 200m, 0.3]);
        var head = Rows(["West", 200.005m, 0.3], ["East", 100m, 0.25]);
        var (measures, diffs) = ResultComparer.Differential(Case, @base, head, new HashSet<string>(), new HashSet<string>());
        Assert.Empty(diffs);
        Assert.All(measures, m => Assert.Equal(MeasureChange.Unchanged, m.Change));
    }

    [Fact]
    public void Missing_rows_count_as_blank_and_edited_measures_are_expected()
    {
        var totalSales = Case.Measures[0].Key;
        var @base = Rows(["East", 100m, 0.25], ["West", 200m, 0.3]);
        var head = Rows(["East", 150m, 0.25], ["North", 10m, null]);
        var (measures, diffs) = ResultComparer.Differential(Case, @base, head, new HashSet<string>(), new HashSet<string> { totalSales });

        Assert.Equal([new MeasureOutcome("Total Sales", MeasureChange.ExpectedChange, 3), new MeasureOutcome("Margin %", MeasureChange.Regression, 1)], measures);
        Assert.False(diffs[0].Expected); // regressions first
        Assert.Equal("West", diffs[0].Key["Store[Region]"]);
        Assert.Equal(0.3, diffs[0].Base);
        Assert.Null(diffs[0].Head);
        Assert.Null(diffs[0].Delta);
        var north = diffs.Single(d => d.Measure == "Total Sales" && (string?)d.Key["Store[Region]"] == "North");
        Assert.Null(north.Base);
        Assert.Equal(10m, north.Head);
    }

    [Fact]
    public void New_measures_are_not_compared()
    {
        var margin = Case.Measures[1].Key;
        var (measures, diffs) = ResultComparer.Differential(Case, Rows(["East", 1m, null]), Rows(["East", 1m, 0.5]), new HashSet<string> { margin }, new HashSet<string>());
        Assert.Equal(MeasureChange.Added, measures[1].Change);
        Assert.Empty(diffs);
    }

    [Fact]
    public void Duplicate_keys_and_wrong_shapes_are_query_errors()
    {
        Assert.Throws<DaxQueryException>(() => Rows(["East", 1m, 0.1], ["East", 2m, 0.2]));
        var ex = Assert.Throws<DaxQueryException>(() => ResultComparer.Index(Query, new DaxResult { Columns = [new("x", "string")], Rows = [], Target = new DaxTarget("x", "y") }));
        Assert.Contains("1 columns; the query asked for 3", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Snapshot_matches_stored_numbers_whatever_their_type()
    {
        var snapshot = new TestAssertion { Type = "snapshot", Columns = ["Store[Region]", "[Total Sales]"], Rows = [new YamlRow(["East", 100L]), new YamlRow(["West", 1.5m])] };
        var c = Case with { Measures = [Case.Measures[0]] };
        var q = QueryPlanner.Values(c, SideDefinitions.Empty, m => m.Name, "a");
        var live = ResultComparer.Index(q, new DaxResult
        {
            Columns = [new("Store[Region]", "string"), new("[Total Sales]", "decimal")],
            Rows = [["East", 100.0m], ["West", 2.5m], ["North", 7m]],
            Target = new DaxTarget("x", "y"),
        });
        var diffs = ResultComparer.Snapshot(c, snapshot, live);
        Assert.Equal(2, diffs.Count);
        Assert.Equal((1.5m, 2.5m), ((decimal)diffs[0].Base!, (decimal)diffs[0].Head!));
        Assert.Equal("North", diffs[1].Key["Store[Region]"]);
        Assert.Null(diffs[1].Base);
    }

    [Theory]
    [InlineData(100.0, 100.004, 0.005, 0, true)]
    [InlineData(100.0, 100.006, 0.005, 0, false)]
    [InlineData(1e9, 1e9 + 5, 0, 1e-8, true)]
    [InlineData(1e9, 1e9 + 50, 0, 1e-8, false)]
    public void Tolerance_is_absolute_or_relative_whichever_is_larger(double a, double b, double abs, double rel, bool equal) =>
        Assert.Equal(equal, DaxValues.Equal(a, b, new Tolerance(abs, rel)));

    [Fact]
    public void Values_normalize_to_a_small_set_of_types()
    {
        Assert.Equal(5L, DaxValues.Normalize(5));
        Assert.Equal("Infinity", DaxValues.Normalize(double.PositiveInfinity));
        Assert.Equal("NaN", DaxValues.Normalize(double.NaN));
        Assert.Equal("2024-01-31T00:00:00", DaxValues.Normalize(new DateTime(2024, 1, 31)));
        Assert.Null(DaxValues.Normalize(DBNull.Value));
        Assert.False(DaxValues.Equal("1", 1L, Tolerance.Default));
        Assert.True(DaxValues.Equal(1L, 1.0m, Tolerance.Default));
        Assert.True(DaxValues.Equal(null, null, Tolerance.Default));
        Assert.False(DaxValues.Equal(0L, null, Tolerance.Default));
    }
}
