using TabularTools.Core.Diff;
using TabularTools.Core.Loading;
using TabularTools.Core.Snapshots;

namespace TabularTools.Core.Tests;

/// <summary>
/// One snapshot edit per change kind that the TMDL-based tests (DiffEngineTests) don't reach. Together with those, every kind
/// in <see cref="SeverityPolicy"/> has a test (checked by <see cref="Every_change_kind_has_a_test"/>).
/// </summary>
public class ChangeKindCoverageTests
{
    private static readonly ModelSnapshot Original = ModelLoader.Load(TestPaths.ExampleSalesSemanticModel).Snapshot;

    private delegate ModelSnapshot Edit(ModelSnapshot s);

    /// <summary>kind → (edit applied to base, edit applied to head).</summary>
    private static readonly Dictionary<string, (Edit Base, Edit Head)> Cases = new()
    {
        ["model.defaultModeChanged"] = (Same, s => s with { DefaultMode = "directQuery" }),
        ["model.cultureChanged"] = (Same, s => s with { Culture = "en-GB" }),
        ["perspective.added"] = (Same, s => s with { Perspectives = ["Basic"] }),
        ["perspective.removed"] = (s => s with { Perspectives = ["Basic"] }, Same),
        ["culture.added"] = (Same, s => s with { Cultures = ["en-GB"] }),
        ["culture.removed"] = (s => s with { Cultures = ["en-GB"] }, Same),

        ["table.kindChanged"] = (Same, s => Table(s, "Store", t => t with { Kind = TableKind.CalculatedTable, Expression = "GENERATESERIES(1, 3)" })),
        ["table.expressionChanged"] = (s => Calc(s, "GENERATESERIES(1, 3)"), s => Calc(s, "GENERATESERIES(1, 4)")),
        ["table.expressionFormatting"] = (s => Calc(s, "GENERATESERIES(1, 3)"), s => Calc(s, "GENERATESERIES ( 1, 3 )")),
        ["table.unhidden"] = (s => Table(s, "Store", t => t with { IsHidden = true }), Same),
        ["table.descriptionChanged"] = (Same, s => Table(s, "Store", t => t with { Description = "Stores." })),
        ["table.dataCategoryChanged"] = (Same, s => Table(s, "Date", t => t with { DataCategory = "Time" })),

        ["column.added"] = (Same, s => Table(s, "Store", t => t with { Columns = [.. t.Columns, NewColumn("Store", "City")] })),
        ["column.removed"] = (s => Table(s, "Store", t => t with { Columns = [.. t.Columns, NewColumn("Store", "City")] }), Same),
        ["column.kindChanged"] = (Same, s => Column(s, "Store", "Region", c => c with { Kind = ColumnKind.Calculated, Expression = "\"West\"", SourceColumn = null })),
        ["column.expressionFormatting"] = (Same, s => Column(s, "Sales", "Product Category", c => c with { Expression = "RELATED ( Product[Category] )" })),
        ["column.isKeyChanged"] = (Same, s => Column(s, "Customer", "CustomerKey", c => c with { IsKey = false })),
        ["column.unhidden"] = (Same, s => Column(s, "Customer", "CustomerKey", c => c with { IsHidden = false })),
        ["column.displayFolderChanged"] = (Same, s => Column(s, "Customer", "Segment", c => c with { DisplayFolder = "Attributes" })),
        ["column.descriptionChanged"] = (Same, s => Column(s, "Customer", "Segment", c => c with { Description = "Customer segment." })),
        ["column.dataCategoryChanged"] = (Same, s => Column(s, "Store", "Region", c => c with { DataCategory = "StateOrProvince" })),

        ["measure.formatStringExpressionChanged"] = (Same, s => Measure(s, "Margin", m => m with { FormatStringExpression = "\"0.0%\"" })),
        ["measure.detailRowsChanged"] = (Same, s => Measure(s, "Margin", m => m with { DetailRowsExpression = "SELECTCOLUMNS(Sales, \"Order\", Sales[SalesOrderNumber])" })),
        ["measure.kpiChanged"] = (Same, s => Measure(s, "Margin", m => m with { HasKpi = true })),
        ["measure.unhidden"] = (s => Measure(s, "Margin", m => m with { IsHidden = true }), Same),

        ["partition.added"] = (Same, s => Table(s, "Product", t => t with { Partitions = [.. t.Partitions, NewPartition("Product", "Archive")] })),
        ["partition.removed"] = (s => Table(s, "Product", t => t with { Partitions = [.. t.Partitions, NewPartition("Product", "Archive")] }), Same),
        ["partition.sourceFormatting"] = (Same, s => Partition(s, "Product", p => p with { Expression = p.Expression!.Replace("Sql.Database(ServerName, DatabaseName)", "Sql.Database( ServerName, DatabaseName )", StringComparison.Ordinal) })),
        ["partition.sourceTypeChanged"] = (Same, s => Partition(s, "Product", p => p with { SourceType = "query", Expression = "SELECT * FROM dbo.DimProduct" })),

        ["hierarchy.added"] = (Same, s => Table(s, "Product", t => t with { Hierarchies = [NewHierarchy("Product", "Catalog")] })),
        ["hierarchy.removed"] = (s => Table(s, "Product", t => t with { Hierarchies = [NewHierarchy("Product", "Catalog")] }), Same),
        ["hierarchy.hidden"] = (Same, s => Hierarchy(s, h => h with { IsHidden = true })),
        ["hierarchy.unhidden"] = (s => Hierarchy(s, h => h with { IsHidden = true }), Same),
        ["hierarchy.displayFolderChanged"] = (Same, s => Hierarchy(s, h => h with { DisplayFolder = "Calendars" })),
        ["hierarchy.descriptionChanged"] = (Same, s => Hierarchy(s, h => h with { Description = "Year, then month." })),

        ["calculationItem.added"] = (Same, s => Table(s, "Time Intelligence", t => t with { CalculationItems = [.. t.CalculationItems, NewCalcItem("QTD", 3)] })),
        ["calculationItem.renamed"] = (Same, s => CalcItem(s, "YTD", c => c with { Name = "Year to date", Key = ObjectKeys.CalculationItem("Time Intelligence", "Year to date") })),
        ["calculationItem.expressionFormatting"] = (Same, s => CalcItem(s, "Current", c => c with { Expression = "SELECTEDMEASURE ( )" })),
        ["calculationItem.ordinalChanged"] = (Same, s => CalcItem(s, "PY", c => c with { Ordinal = 9 })),
        ["calculationItem.formatStringExpressionChanged"] = (Same, s => CalcItem(s, "PY", c => c with { FormatStringExpression = "SELECTEDMEASUREFORMATSTRING()" })),
        ["calculationItem.descriptionChanged"] = (Same, s => CalcItem(s, "PY", c => c with { Description = "Prior year." })),

        ["relationship.added"] = (s => s with { Relationships = s.Relationships.Where(r => r.ToTable != "Store").ToList() }, Same),
        ["relationship.securityFilteringChanged"] = (Same, s => Relationship(s, "Store", r => r with { SecurityFilteringBehavior = "bothDirections" })),
        ["relationship.referentialIntegrityChanged"] = (Same, s => Relationship(s, "Store", r => r with { RelyOnReferentialIntegrity = true })),

        ["role.added"] = (Same, s => s with { Roles = [.. s.Roles, new RoleSnapshot(ObjectKeys.Role("East"), "East", "read", null, [], 0)] }),
        ["role.rlsFilterAdded"] = (s => Permission(s, p => p with { FilterExpression = null }), Same),
        ["role.rlsFilterFormatting"] = (Same, s => Permission(s, p => p with { FilterExpression = "[Region]  =  \"West\"" })),
        ["role.olsChanged"] = (Same, s => Permission(s, p => p with { ColumnPermissions = [new ColumnPermissionSnapshot("Store Name", "none")] })),
        ["role.membersChanged"] = (Same, s => Role(s, r => r with { MemberCount = 2 })),
        ["role.descriptionChanged"] = (Same, s => Role(s, r => r with { Description = "Western stores only." })),

        ["expression.added"] = (Same, s => s with { Expressions = [.. s.Expressions, NewExpression("Region")] }),
        ["expression.removed"] = (s => s with { Expressions = [.. s.Expressions, NewExpression("Region")] }, Same),
        ["expression.renamed"] = (Same, s => Expression(s, "DatabaseName", e => e with { Name = "Database", Key = ObjectKeys.Expression("Database") })),
        ["expression.expressionFormatting"] = (Same, s => Expression(s, "DatabaseName", e => e with { Expression = e.Expression.Replace("meta [", "meta  [", StringComparison.Ordinal) })),
        ["expression.kindChanged"] = (Same, s => Expression(s, "DatabaseName", e => e with { Kind = "dax" })),
        ["expression.descriptionChanged"] = (Same, s => Expression(s, "DatabaseName", e => e with { Description = "Warehouse database." })),

        ["function.added"] = (Same, WithFunction),
        ["function.removed"] = (WithFunction, Same),
        ["function.renamed"] = (WithFunction, s => Function(WithFunction(s), f => f with { Name = "Pct", Key = ObjectKeys.Function("Pct") })),
        ["function.expressionChanged"] = (WithFunction, s => Function(WithFunction(s), f => f with { Expression = "(x: NUMERIC) => x * 100" })),
        ["function.expressionFormatting"] = (WithFunction, s => Function(WithFunction(s), f => f with { Expression = "( x : NUMERIC ) => DIVIDE ( x, 100 )" })),
        ["function.hidden"] = (WithFunction, s => Function(WithFunction(s), f => f with { IsHidden = true })),
        ["function.unhidden"] = (s => Function(WithFunction(s), f => f with { IsHidden = true }), WithFunction),
        ["function.descriptionChanged"] = (WithFunction, s => Function(WithFunction(s), f => f with { Description = "Percent." })),
    };

    public static TheoryData<string> Kinds => new(Cases.Keys.Order(StringComparer.Ordinal));

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Each_edit_produces_exactly_its_change_kind(string kind)
    {
        var (editBase, editHead) = Cases[kind];
        var outcome = DiffEngine.Compare(new DiffInput(editBase(Original)), new DiffInput(editHead(Original)), new DiffOptions { IncludeFormattingOnly = true });

        var change = Assert.Single(outcome.Changes);
        Assert.Equal(kind, change.Kind);
        Assert.Equal(SeverityPolicy.Default(kind), change.Severity);
    }

    [Fact]
    public void Every_change_kind_has_a_test()
    {
        var source = string.Concat(Directory.GetFiles(Path.Combine(TestPaths.RepoRoot, "tests"), "*.cs", SearchOption.AllDirectories).Select(File.ReadAllText));
        var untested = SeverityPolicy.Kinds.Select(k => k.Kind).Where(k => !source.Contains($"\"{k}\"", StringComparison.Ordinal)).ToList();
        Assert.Empty(untested);
    }

    // ---- Edit helpers ----

    private static ModelSnapshot Same(ModelSnapshot s) => s;

    private static ModelSnapshot Table(ModelSnapshot s, string name, Func<TableSnapshot, TableSnapshot> f) =>
        s with { Tables = s.Tables.Select(t => t.Name == name ? f(t) : t).ToList() };

    private static ModelSnapshot Calc(ModelSnapshot s, string expression) =>
        Table(s, "Store", t => t with { Kind = TableKind.CalculatedTable, Expression = expression });

    private static ModelSnapshot Column(ModelSnapshot s, string table, string name, Func<ColumnSnapshot, ColumnSnapshot> f) =>
        Table(s, table, t => t with { Columns = t.Columns.Select(c => c.Name == name ? f(c) : c).ToList() });

    private static ModelSnapshot Measure(ModelSnapshot s, string name, Func<MeasureSnapshot, MeasureSnapshot> f) =>
        Table(s, "Sales", t => t with { Measures = t.Measures.Select(m => m.Name == name ? f(m) : m).ToList() });

    private static ModelSnapshot Partition(ModelSnapshot s, string table, Func<PartitionSnapshot, PartitionSnapshot> f) =>
        Table(s, table, t => t with { Partitions = t.Partitions.Select(f).ToList() });

    private static ModelSnapshot Hierarchy(ModelSnapshot s, Func<HierarchySnapshot, HierarchySnapshot> f) =>
        Table(s, "Date", t => t with { Hierarchies = t.Hierarchies.Select(f).ToList() });

    private static ModelSnapshot CalcItem(ModelSnapshot s, string name, Func<CalculationItemSnapshot, CalculationItemSnapshot> f) =>
        Table(s, "Time Intelligence", t => t with { CalculationItems = t.CalculationItems.Select(c => c.Name == name ? f(c) : c).ToList() });

    private static ModelSnapshot Relationship(ModelSnapshot s, string toTable, Func<RelationshipSnapshot, RelationshipSnapshot> f) =>
        s with { Relationships = s.Relationships.Select(r => r.ToTable == toTable ? f(r) : r).ToList() };

    private static ModelSnapshot Role(ModelSnapshot s, Func<RoleSnapshot, RoleSnapshot> f) => s with { Roles = s.Roles.Select(f).ToList() };

    private static ModelSnapshot Permission(ModelSnapshot s, Func<TablePermissionSnapshot, TablePermissionSnapshot> f) =>
        Role(s, r => r with { TablePermissions = r.TablePermissions.Select(f).ToList() });

    private static ModelSnapshot Expression(ModelSnapshot s, string name, Func<NamedExpressionSnapshot, NamedExpressionSnapshot> f) =>
        s with { Expressions = s.Expressions.Select(e => e.Name == name ? f(e) : e).ToList() };

    private static ModelSnapshot WithFunction(ModelSnapshot s) =>
        s with { Functions = [new FunctionSnapshot(ObjectKeys.Function("Percent"), "Percent", "(x: NUMERIC) => DIVIDE(x, 100)", null, false, "00000000-0000-4000-8000-000000009001")] };

    private static ModelSnapshot Function(ModelSnapshot s, Func<FunctionSnapshot, FunctionSnapshot> f) => s with { Functions = s.Functions.Select(f).ToList() };

    private static ColumnSnapshot NewColumn(string table, string name) =>
        new(ObjectKeys.Column(table, name), table, name, ColumnKind.Data, "string", "none", null, false, false, null, null, null, null, name, null, null);

    private static PartitionSnapshot NewPartition(string table, string name) =>
        new(ObjectKeys.Partition(table, name), table, name, "m", "import", "#table({\"ProductKey\"}, {})", null);

    private static HierarchySnapshot NewHierarchy(string table, string name) =>
        new(ObjectKeys.Hierarchy(table, name), table, name, false, null, null, [new LevelSnapshot("Category", 0, "Category"), new LevelSnapshot("Product", 1, "Product Name")], null);

    private static CalculationItemSnapshot NewCalcItem(string name, int ordinal) =>
        new(ObjectKeys.CalculationItem("Time Intelligence", name), "Time Intelligence", name, "CALCULATE(SELECTEDMEASURE(), DATESQTD('Date'[Date]))", ordinal, null, null);

    private static NamedExpressionSnapshot NewExpression(string name) =>
        new(ObjectKeys.Expression(name), name, "m", "\"West\" meta [IsParameterQuery=true, Type=\"Text\"]", true, null, null);
}
