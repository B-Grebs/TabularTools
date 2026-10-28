using TabularTools.Core.Diff;
using TabularTools.Core.Loading;
using TabularTools.Core.Snapshots;
using TabularTools.Core.Testing;

namespace TabularTools.Core.Tests;

public sealed class OverridePlanTests
{
    private static OverridePlan Plan(string basePath, string headPath, bool columnOverrides = false)
    {
        var b = ModelLoader.Load(basePath);
        var h = ModelLoader.Load(headPath);
        var outcome = DiffEngine.Compare(new DiffInput(b.Snapshot, b.Dependencies), new DiffInput(h.Snapshot, h.Dependencies));
        return OverridePlan.Build(b.Snapshot, h.Snapshot, outcome.Match, outcome.Changes, columnOverrides, b.Dependencies, h.Dependencies);
    }

    private static string V(string version) => Path.Combine(GitFixture.DiffFixture(version), GitFixture.ModelPath);

    [Fact]
    public void V1_to_v2_overrides_edited_renamed_added_and_removed_measures_only()
    {
        var plan = Plan(V("ExampleSales-v1"), V("ExampleSales-v2"));

        Assert.Equal(["Total Sales", "Total Quantity", "Average Price"], plan.Base.Measures.Select(d => d.Name));
        Assert.Equal(["Total Sales", "Units Sold", "Promotion Count"], plan.Head.Measures.Select(d => d.Name));
        Assert.Equal([ObjectKeys.Measure("Sales", "Total Sales")], plan.DirectlyEdited);
        Assert.Equal("Total Quantity", plan.BaseNames[ObjectKeys.Measure("Sales", "Units Sold")]);
        Assert.Equal(ObjectKeys.Measure("Sales", "Customer Count"), plan.BaseKeys[ObjectKeys.Measure("Customer", "Customer Count")]);
        Assert.Contains(ObjectKeys.Measure("Promotion", "Promotion Count"), plan.Added);
        // 'Sales YoY %' only changed formatting, and 'Customer Count' only moved: neither is overridden.
        Assert.DoesNotContain(plan.Head.Measures, d => d.Name is "Sales YoY %" or "Customer Count");
        Assert.Empty(plan.Head.Columns);
    }

    [Fact]
    public void Each_query_only_defines_what_it_depends_on()
    {
        var plan = Plan(V("ExampleSales-v1"), V("ExampleSales-v2"));

        // Margin % depends on Total Sales, so a query for it carries the Total Sales override; nothing else.
        var margin = plan.HeadFor([ObjectKeys.Measure("Sales", "Margin %")]);
        Assert.Equal(["Total Sales"], margin.Measures.Select(d => d.Name));
        Assert.True(plan.HeadFor([ObjectKeys.Measure("Sales", "Total Cost")]).IsEmpty);
        Assert.True(plan.HeadFor([ObjectKeys.Measure("Customer", "Customer Count")]).IsEmpty);
        Assert.Equal(["Units Sold"], plan.HeadFor([ObjectKeys.Measure("Sales", "Units Sold")]).Measures.Select(d => d.Name));
        Assert.Equal(["Total Quantity"], plan.BaseFor([ObjectKeys.Measure("Sales", "Total Quantity")]).Measures.Select(d => d.Name));
    }

    [Fact]
    public void Calculated_column_changes_are_structural_unless_column_overrides_are_on()
    {
        using var head = new EditedModel().Replace("tables/Sales.tmdl", "column 'Product Category' = RELATED(Product[Category])", "column 'Product Category' = UPPER(RELATED(Product[Category]))");

        var off = Plan(TestPaths.ExampleSalesSemanticModel, head.Path);
        Assert.Empty(off.Head.Columns);
        Assert.Contains(off.StructuralChangesNotTested, c => c.Kind == "column.expressionChanged");

        var on = Plan(TestPaths.ExampleSalesSemanticModel, head.Path, columnOverrides: true);
        var column = Assert.Single(on.Head.Columns);
        Assert.Equal("UPPER(RELATED(Product[Category]))", column.Expression);
        Assert.Equal("RELATED(Product[Category])", Assert.Single(on.Base.Columns).Expression);
        Assert.DoesNotContain(on.StructuralChangesNotTested, c => c.Kind == "column.expressionChanged");
    }

    [Fact]
    public void A_plan_without_base_overrides_nothing_and_treats_every_measure_as_new()
    {
        var model = ModelLoader.Load(TestPaths.ExampleSalesSemanticModel);
        var plan = OverridePlan.HeadOnly(model.Snapshot);
        Assert.True(plan.NoBase);
        Assert.True(plan.HeadFor(model.Snapshot.AllMeasures.Select(m => m.Key)).IsEmpty);
        Assert.Equal(model.Snapshot.AllMeasures.Count(), plan.Added.Count);
    }
}

public sealed class TestCoverageTests
{
    [Fact]
    public void Every_change_kind_is_classified_and_the_explicit_lists_only_name_real_kinds()
    {
        Assert.All(TestCoverage.ExplicitKinds, k => Assert.True(SeverityPolicy.IsKnown(k), $"{k} isn't a change kind"));

        // High and breaking changes that tests treat as not affecting values. Reviewed: hierarchies and role membership
        // don't change what a DAX query returns. A new kind of high severity has to be added here or classified.
        var highWithoutValueEffect = SeverityPolicy.Kinds
            .Where(k => k.Severity >= ChangeSeverity.High && TestCoverage.Classify(k.Kind) == ChangeCoverage.NoValueEffect)
            .Select(k => k.Kind).Order(StringComparer.Ordinal);
        Assert.Equal(["hierarchy.removed", "hierarchy.renamed", "role.added", "role.membersChanged"], highWithoutValueEffect);

        Assert.Equal(ChangeCoverage.Simulated, TestCoverage.Classify("measure.expressionChanged"));
        Assert.Equal(ChangeCoverage.NotSimulated, TestCoverage.Classify("relationship.crossFilterChanged"));
        Assert.Equal(ChangeCoverage.NoValueEffect, TestCoverage.Classify("measure.formatStringChanged"));
    }
}
