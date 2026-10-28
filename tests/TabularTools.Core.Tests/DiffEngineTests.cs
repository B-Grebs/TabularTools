using TabularTools.Core.Diff;
using TabularTools.Core.Loading;

namespace TabularTools.Core.Tests;

public class DiffEngineTests
{
    private static readonly string V1 = Path.Combine(GitFixture.DiffFixture("ExampleSales-v1"), GitFixture.ModelPath);
    private static readonly string V2 = Path.Combine(GitFixture.DiffFixture("ExampleSales-v2"), GitFixture.ModelPath);

    private static DiffOutcome Compare(string basePath, string headPath, DiffOptions? options = null) =>
        DiffEngine.Compare(Side(basePath), Side(headPath), options);

    private static DiffInput Side(string path) => ModelSides.FromDisk(path, TestPaths.Fixtures)!.ToInput();

    /// <summary>The v1 → v2 fixture contract (ARCHITECTURE §17.1): every planted change, with its severity.</summary>
    public static TheoryData<string, string, ChangeSeverity> FixtureChanges => new()
    {
        { "measure.expressionChanged", "table:Sales/measure:Total Sales", ChangeSeverity.High },
        { "relationship.crossFilterChanged", "relationship:Sales[ProductKey]->Product[ProductKey]", ChangeSeverity.High },
        { "relationship.cardinalityChanged", "relationship:Sales[StoreKey]->Store[StoreKey]", ChangeSeverity.High },
        { "column.dataTypeChanged", "table:Product/column:List Price", ChangeSeverity.High },
        { "column.summarizeByChanged", "table:Sales/column:Quantity", ChangeSeverity.Medium },
        { "column.hidden", "table:Sales/column:CustomerKey", ChangeSeverity.Medium },
        { "role.rlsFilterChanged", "role:West Region/tablePermission:Store", ChangeSeverity.Breaking },
        { "measure.renamed", "table:Sales/measure:Units Sold", ChangeSeverity.Breaking },
        { "column.renamed", "table:Customer/column:Customer Full Name", ChangeSeverity.Breaking },
        { "measure.moved", "table:Customer/measure:Customer Count", ChangeSeverity.Breaking },
        { "measure.removed", "table:Sales/measure:Average Price", ChangeSeverity.Breaking },
        { "measure.displayFolderChanged", "table:Sales/measure:Sales PY", ChangeSeverity.Low },
        { "table.added", "table:Promotion", ChangeSeverity.Info },
    };

    [Theory]
    [MemberData(nameof(FixtureChanges))]
    public void Every_fixture_change_is_detected_with_the_expected_severity(string kind, string key, ChangeSeverity severity)
    {
        var outcome = Compare(V1, V2);
        var change = Assert.Single(outcome.Changes, c => c.Kind == kind && c.Object.Key == key);
        Assert.Equal(severity, change.Severity);
    }

    [Fact]
    public void Nothing_else_is_reported_and_the_formatting_only_change_is_hidden()
    {
        var outcome = Compare(V1, V2);
        Assert.Equal(FixtureChanges.Count, outcome.Changes.Count);
        Assert.Equal(1, outcome.FormattingOnlyHidden);
        Assert.DoesNotContain(outcome.Changes, c => c.Object.Key == "table:Sales/measure:Sales YoY %");
    }

    [Fact]
    public void Formatting_only_changes_are_reported_as_info_when_asked()
    {
        var outcome = Compare(V1, V2, new DiffOptions { IncludeFormattingOnly = true });
        var change = Assert.Single(outcome.Changes, c => c.Object.Key == "table:Sales/measure:Sales YoY %");
        Assert.Equal("measure.expressionFormatting", change.Kind);
        Assert.Equal(ChangeSeverity.Info, change.Severity);
        Assert.True(change.Details!.FormattingOnly);
        Assert.Equal(0, outcome.FormattingOnlyHidden);
    }

    [Fact]
    public void Renames_state_their_confidence_and_evidence()
    {
        var outcome = Compare(V1, V2);
        var measure = outcome.Changes.Single(c => c.Kind == "measure.renamed").Rename!;
        Assert.Equal(("Total Quantity", "Units Sold", "certain", "lineageTag"), (measure.From, measure.To, measure.Confidence, measure.Evidence));

        var column = outcome.Changes.Single(c => c.Kind == "column.renamed").Rename!;
        Assert.Equal(("Customer Name", "Customer Full Name", "high", "sourceColumn"), (column.From, column.To, column.Confidence, column.Evidence));
        Assert.Equal(0.9, column.Score);
    }

    [Fact]
    public void Expression_changes_carry_a_line_diff_and_downstream_impact()
    {
        var change = Compare(V1, V2).Changes.Single(c => c.Kind == "measure.expressionChanged");
        Assert.Equal("SUM(Sales[Amount])", change.Before);
        Assert.Equal("@@ -1 +1 @@\n-SUM(Sales[Amount])\n+SUMX(Sales, Sales[Quantity] * Sales[Unit Price])", change.Details!.ExpressionDiff);
        Assert.Contains("table:Sales/measure:Margin", change.Impact!.Downstream);
        Assert.Contains("table:Sales/measure:Sales YoY %", change.Impact.Downstream); // transitive, through Sales PY
        Assert.Equal(change.Impact.Downstream.Count, change.Impact.DownstreamCount);
    }

    [Fact]
    public void Changes_point_at_the_tmdl_file_and_line()
    {
        var changes = Compare(V1, V2).Changes;
        Assert.Equal(new SourceLocation("diff/ExampleSales-v2/ExampleSales.SemanticModel/definition/tables/Sales.tmdl", 6),
            changes.Single(c => c.Kind == "measure.expressionChanged").Location);
        Assert.Equal(new SourceLocation("diff/ExampleSales-v2/ExampleSales.SemanticModel/definition/roles/West Region.tmdl", 4),
            changes.Single(c => c.Kind == "role.rlsFilterChanged").Location);
        // Removed objects point at the base version.
        Assert.Equal(new SourceLocation("diff/ExampleSales-v1/ExampleSales.SemanticModel/definition/tables/Sales.tmdl", 33),
            changes.Single(c => c.Kind == "measure.removed").Location);
    }

    [Fact]
    public void Changes_are_ordered_most_severe_first()
    {
        var severities = Compare(V1, V2).Changes.Select(c => c.Severity).ToList();
        Assert.Equal(severities.OrderByDescending(s => s), severities);
    }

    [Fact]
    public void Severity_overrides_and_min_severity_apply()
    {
        var outcome = Compare(V1, V2, new DiffOptions
        {
            SeverityOverrides = new Dictionary<string, ChangeSeverity> { ["column.summarizeByChanged"] = ChangeSeverity.Breaking },
            MinSeverity = ChangeSeverity.Breaking,
        });
        Assert.All(outcome.Changes, c => Assert.Equal(ChangeSeverity.Breaking, c.Severity));
        Assert.Contains(outcome.Changes, c => c.Kind == "column.summarizeByChanged");
    }

    [Fact]
    public void Identical_models_have_no_changes()
    {
        var outcome = Compare(V1, TestPaths.ExampleSalesSemanticModel);
        Assert.Empty(outcome.Changes);
        Assert.Equal(0, outcome.FormattingOnlyHidden);
    }

    [Fact]
    public void A_model_that_only_exists_on_one_side_is_all_additions_or_all_removals()
    {
        var added = DiffEngine.Compare(new DiffInput(null), Side(V1));
        Assert.All(added.Changes, c => Assert.EndsWith(".added", c.Kind));
        Assert.Contains(added.Changes, c => c.Kind == "table.added" && c.Object.Name == "Sales");
        Assert.DoesNotContain(added.Changes, c => c.Kind == "measure.added"); // counted on their table

        var removed = DiffEngine.Compare(Side(V1), new DiffInput(null));
        Assert.Contains(removed.Changes, c => c.Kind == "table.removed" && c.Severity == ChangeSeverity.Breaking);
        Assert.Contains(removed.Changes, c => c.Kind == "role.removed");
    }

    /// <summary>One targeted edit of the ExampleSales model per change kind not covered by the v1 → v2 fixture.</summary>
    public static TheoryData<string, string, string, string, string> SingleEdits => new()
    {
        { "tables/Sales.tmdl", "formatString: #,0.00\n\t\tdisplayFolder: Sales\n\t\tlineageTag: 00000000-0000-4000-8000-000000001101", "formatString: #,0\n\t\tdisplayFolder: Sales\n\t\tlineageTag: 00000000-0000-4000-8000-000000001101", "measure.formatStringChanged", "table:Sales/measure:Total Sales" },
        { "tables/Sales.tmdl", "/// Sum of the line amount.", "/// Sum of the order line amounts.", "measure.descriptionChanged", "table:Sales/measure:Total Sales" },
        { "tables/Sales.tmdl", "measure 'Total Quantity' = SUM(Sales[Quantity])\n\t\tformatString: #,0", "measure 'Total Quantity' = SUM(Sales[Quantity])\n\t\tisHidden\n\t\tformatString: #,0", "measure.hidden", "table:Sales/measure:Total Quantity" },
        { "tables/Sales.tmdl", "column 'Product Category' = RELATED(Product[Category])", "column 'Product Category' = UPPER(RELATED(Product[Category]))", "column.expressionChanged", "table:Sales/column:Product Category" },
        { "tables/Sales.tmdl", "sourceColumn: LoadBatchId", "sourceColumn: BatchId", "column.sourceColumnChanged", "table:Sales/column:LoadBatchId" },
        { "tables/Date.tmdl", "sourceColumn: MonthName\n\t\tsortByColumn: 'Month Number'", "sourceColumn: MonthName", "column.sortByChanged", "table:Date/column:Month Name" },
        { "tables/Date.tmdl", "dataType: int64\n\t\tisHidden\n\t\tlineageTag: 00000000-0000-4000-8000-000000002201", "dataType: string\n\t\tisHidden\n\t\tlineageTag: 00000000-0000-4000-8000-000000002201", "column.keyDataTypeChanged", "table:Date/column:DateKey" },
        { "tables/Date.tmdl", "column Year\n\t\tdataType: int64\n\t\tformatString: 0", "column Year\n\t\tdataType: int64\n\t\tformatString: #", "column.formatStringChanged", "table:Date/column:Year" },
        { "tables/Date.tmdl", "\t\tlevel Month\n\t\t\tlineageTag: 00000000-0000-4000-8000-000000002303\n\t\t\tcolumn: 'Month Name'\n", "", "hierarchy.levelsChanged", "table:Date/hierarchy:Calendar" },
        { "tables/Date.tmdl", "hierarchy Calendar", "hierarchy 'Year-Month'", "hierarchy.renamed", "table:Date/hierarchy:Year-Month" },
        { "tables/Product.tmdl", "column 'List Price'\n\t\tdataType: decimal\n\t\tformatString: #,0.00\n\t\tlineageTag: 00000000-0000-4000-8000-000000003204\n\t\tsummarizeBy: none", "column 'List Price'\n\t\tdataType: decimal\n\t\tformatString: #,0.00\n\t\tlineageTag: 00000000-0000-4000-8000-000000003204\n\t\tsummarizeBy: sum", "column.summarizeByChanged", "table:Product/column:List Price" },
        { "tables/Product.tmdl", "Item=\"DimProduct\"", "Item=\"DimProduct_v2\"", "partition.sourceChanged", "table:Product/partition:Product" },
        { "tables/Product.tmdl", "partition Product = m\n\t\tmode: import", "partition Product = m\n\t\tmode: directQuery", "partition.modeChanged", "table:Product/partition:Product" },
        { "tables/Store.tmdl", "table Store\n", "table Store\n\tisHidden\n", "table.hidden", "table:Store" },
        { "tables/Time Intelligence.tmdl", "precedence: 1", "precedence: 2", "calculationGroup.precedenceChanged", "table:Time Intelligence" },
        { "tables/Time Intelligence.tmdl", "calculationItem YTD = CALCULATE(SELECTEDMEASURE(), DATESYTD('Date'[Date]))", "calculationItem YTD = CALCULATE(SELECTEDMEASURE(), DATESQTD('Date'[Date]))", "calculationItem.expressionChanged", "table:Time Intelligence/calculationItem:YTD" },
        { "tables/Time Intelligence.tmdl", "\t\tcalculationItem PY = CALCULATE(SELECTEDMEASURE(), SAMEPERIODLASTYEAR('Date'[Date]))\n", "", "calculationItem.removed", "table:Time Intelligence/calculationItem:PY" },
        { "relationships.tmdl", "relationship 00000000-0000-4000-8000-000000000201\n", "relationship 00000000-0000-4000-8000-000000000201\n\tisActive: false\n", "relationship.activeChanged", "relationship:Sales[DateKey]->Date[DateKey]" },
        { "relationships.tmdl", "relationship 00000000-0000-4000-8000-000000000204\n\tfromColumn: Sales.StoreKey\n\ttoColumn: Store.StoreKey\n", "", "relationship.removed", "relationship:Sales[StoreKey]->Store[StoreKey]" },
        { "roles/West Region.tmdl", "modelPermission: read", "modelPermission: readRefresh", "role.modelPermissionChanged", "role:West Region" },
        { "roles/West Region.tmdl", "\ttablePermission Store = [Region] = \"West\"\n", "", "role.rlsFilterRemoved", "role:West Region/tablePermission:Store" },
        { "expressions.tmdl", "\"SalesDW\" meta", "\"SalesDW_Test\" meta", "expression.expressionChanged", "expression:DatabaseName" },
        { "model.tmdl", "\tdiscourageImplicitMeasures\n", "", "model.discourageImplicitMeasuresChanged", "model" },
        { "database.tmdl", "compatibilityLevel: 1600", "compatibilityLevel: 1601", "model.compatibilityLevelChanged", "model" },
    };

    [Theory]
    [MemberData(nameof(SingleEdits))]
    public void Single_edits_produce_the_expected_change_kind(string file, string find, string replace, string kind, string key)
    {
        using var edited = new EditedModel().Replace(file, find, replace);
        var outcome = Compare(TestPaths.ExampleSalesSemanticModel, edited.Path);
        var change = Assert.Single(outcome.Changes, c => c.Kind == kind);
        Assert.Equal(key, change.Object.Key);
        Assert.Equal(SeverityPolicy.Default(kind), change.Severity);
    }

    [Fact]
    public void Relationships_and_children_follow_a_renamed_table()
    {
        using var edited = new EditedModel()
            .Replace("tables/Product.tmdl", "table Product", "table Products")
            .Replace("relationships.tmdl", "toColumn: Product.ProductKey", "toColumn: Products.ProductKey")
            .Replace("tables/Sales.tmdl", "RELATED(Product[Category])", "RELATED(Products[Category])");
        var outcome = Compare(TestPaths.ExampleSalesSemanticModel, edited.Path);

        Assert.Equal(["column.expressionChanged", "table.renamed"], outcome.Changes.Select(c => c.Kind).Order());
        Assert.Equal("table:Products/column:Category", outcome.Match.KeyMap["table:Product/column:Category"]);
        Assert.Equal("relationship:Sales[ProductKey]->Products[ProductKey]", outcome.Match.KeyMap["relationship:Sales[ProductKey]->Product[ProductKey]"]);
    }

    [Fact]
    public void A_renamed_measure_without_a_lineage_tag_is_matched_by_its_expression()
    {
        using var edited = new EditedModel().Replace("tables/Sales.tmdl",
            "measure 'Total Cost' = SUMX(Sales, Sales[Quantity] * Sales[Unit Cost])\n\t\tformatString: #,0.00\n\t\tdisplayFolder: Sales\n\t\tlineageTag: 00000000-0000-4000-8000-000000001103",
            "measure 'Cost' = SUMX(Sales, Sales[Quantity] * Sales[Unit Cost])\n\t\tformatString: #,0.00\n\t\tdisplayFolder: Sales");
        var change = Assert.Single(Compare(TestPaths.ExampleSalesSemanticModel, edited.Path).Changes, c => c.Kind == "measure.renamed");
        Assert.Equal(new RenameInfo("Total Cost", "Cost", "high", "expression", 0.95), change.Rename);
    }

    [Fact]
    public void Unrelated_objects_are_not_mistaken_for_renames()
    {
        using var edited = new EditedModel().Replace("tables/Sales.tmdl",
            "measure 'Total Cost' = SUMX(Sales, Sales[Quantity] * Sales[Unit Cost])\n\t\tformatString: #,0.00\n\t\tdisplayFolder: Sales\n\t\tlineageTag: 00000000-0000-4000-8000-000000001103",
            "measure 'Order Count' = DISTINCTCOUNT(Sales[SalesOrderNumber])\n\t\tformatString: #,0");
        var kinds = Compare(TestPaths.ExampleSalesSemanticModel, edited.Path).Changes.Select(c => c.Kind).Order().ToList();
        Assert.Contains("measure.added", kinds);
        Assert.Contains("measure.removed", kinds);
        Assert.DoesNotContain("measure.renamed", kinds);
    }
}
