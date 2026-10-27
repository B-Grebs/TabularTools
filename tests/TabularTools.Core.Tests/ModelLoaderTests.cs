using TabularTools.Core.Diagnostics;
using TabularTools.Core.Loading;
using TabularTools.Core.Snapshots;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace TabularTools.Core.Tests;

public class ModelLoaderTests
{
    private static readonly Lazy<LoadedModel> Fixture = new(() => ModelLoader.Load(TestPaths.ExampleSalesPbip));

    private static ModelSnapshot Snapshot => Fixture.Value.Snapshot;

    [Fact]
    public void Loads_the_fixture_and_names_the_database_after_the_folder()
    {
        var model = Fixture.Value;

        Assert.Equal("ExampleSales", model.Database.Name);
        Assert.Equal("ExampleSales", Snapshot.Name);
        Assert.Equal(1600, Snapshot.CompatibilityLevel);
        Assert.Equal("en-US", Snapshot.Culture);
        Assert.True(Snapshot.DiscourageImplicitMeasures);
        Assert.Matches("^[0-9a-f]{64}$", model.ContentHash);
    }

    [Fact]
    public void Counts_match_the_fixture()
    {
        Assert.Equal(6, Snapshot.Tables.Count);
        Assert.Equal(11, Snapshot.Tables.Single(t => t.Name == "Sales").Columns.Count);
        Assert.Equal(11, Snapshot.AllMeasures.Count());
        Assert.Equal(4, Snapshot.Relationships.Count);
        Assert.Single(Snapshot.Roles);
        Assert.Equal(2, Snapshot.Expressions.Count);
    }

    [Fact]
    public void Relationships_are_keyed_by_endpoints_and_keep_filter_direction()
    {
        var customer = Snapshot.Relationships.Single(r => r.ToTable == "Customer");

        Assert.Equal("relationship:Sales[CustomerKey]->Customer[CustomerKey]", customer.Key);
        Assert.Equal("bothDirections", customer.CrossFilteringBehavior);
        Assert.Equal("many", customer.FromCardinality);
        Assert.Equal("one", customer.ToCardinality);
        Assert.True(customer.IsActive);
    }

    [Fact]
    public void Table_kinds_and_column_kinds_are_recognised()
    {
        var timeIntelligence = Snapshot.Tables.Single(t => t.Name == "Time Intelligence");
        Assert.Equal(TableKind.CalculationGroup, timeIntelligence.Kind);
        Assert.Equal(1, timeIntelligence.CalculationGroupPrecedence);
        Assert.Equal(["Current", "YTD", "PY"], timeIntelligence.CalculationItems.Select(c => c.Name));

        var category = Snapshot.AllColumns.Single(c => c.Name == "Product Category");
        Assert.Equal(ColumnKind.Calculated, category.Kind);
        Assert.Equal("RELATED(Product[Category])", category.Expression);

        var amount = Snapshot.AllColumns.Single(c => c.Key == "table:Sales/column:Amount");
        Assert.Equal("double", amount.DataType);
        Assert.Equal("sum", amount.SummarizeBy);
        Assert.Equal("Amount", amount.SourceColumn);
        Assert.Equal("00000000-0000-4000-8000-000000001209", amount.LineageTag);
    }

    [Fact]
    public void Measures_partitions_hierarchies_roles_and_parameters_are_captured()
    {
        var totalSales = Snapshot.AllMeasures.Single(m => m.Name == "Total Sales");
        Assert.Equal("table:Sales/measure:Total Sales", totalSales.Key);
        Assert.Equal("SUM(Sales[Amount])", totalSales.Expression);
        Assert.Equal("#,0.00", totalSales.FormatString);
        Assert.Equal("Sum of the line amount.", totalSales.Description);

        var sales = Snapshot.AllPartitions.Single(p => p.Table == "Sales");
        Assert.Equal("m", sales.SourceType);
        Assert.Equal("import", sales.Mode);
        Assert.Contains("Sql.Database(ServerName, DatabaseName)", sales.Expression);

        var calendar = Snapshot.AllHierarchies.Single();
        Assert.Equal(["Year", "Month"], calendar.Levels.Select(l => l.Name));
        Assert.Equal("Month Name", calendar.Levels[1].Column);

        var role = Snapshot.Roles.Single();
        Assert.Equal("read", role.ModelPermission);
        Assert.Equal("[Region] = \"West\"", Assert.Single(role.TablePermissions).FilterExpression);

        Assert.All(Snapshot.Expressions, e => Assert.True(e.IsParameter));
    }

    [Fact]
    public void Tmsl_file_loads_the_same_model()
    {
        using var temp = new TempDir();
        var tmsl = TOM.JsonSerializer.SerializeDatabase(Fixture.Value.Database, new TOM.SerializeOptions { IgnoreTimestamps = true });
        var bim = temp.Combine("model.bim");
        File.WriteAllText(bim, tmsl);

        var loaded = ModelLoader.Load(bim);

        Assert.Equal(ModelFormat.Tmsl, loaded.Source.Format);
        Assert.Equal(Snapshot.AllMeasures.Select(m => m.Key), loaded.Snapshot.AllMeasures.Select(m => m.Key));
        Assert.Equal(Snapshot.Relationships.Select(r => r.Key), loaded.Snapshot.Relationships.Select(r => r.Key));
    }

    [Fact]
    public void Tmdl_format_error_reports_file_and_line()
    {
        using var temp = new TempDir(TestPaths.ExampleSalesSemanticModel);
        var dateFile = temp.Combine("definition", "tables", "Date.tmdl");
        File.WriteAllText(dateFile, File.ReadAllText(dateFile).Replace("summarizeBy: none", "summarizeBy: sideways"));

        var ex = Assert.Throws<ModelLoadException>(() => ModelLoader.Load(temp.Path));

        var diagnostic = Assert.Single(ex.Diagnostics);
        Assert.Equal(DiagnosticCodes.TmdlFormat, diagnostic.Code);
        Assert.Equal(dateFile, diagnostic.File);
        Assert.Equal(9, diagnostic.Line);
        Assert.Contains("sideways", diagnostic.Message);
        Assert.False(ex.IsResolutionFailure);
    }

    [Fact]
    public void Dangling_reference_is_reported()
    {
        using var temp = new TempDir(TestPaths.ExampleSalesSemanticModel);
        var relationships = temp.Combine("definition", "relationships.tmdl");
        File.WriteAllText(relationships, File.ReadAllText(relationships).Replace("fromColumn: Sales.StoreKey", "fromColumn: Sales.NoSuchColumn"));

        var ex = Assert.Throws<ModelLoadException>(() => ModelLoader.Load(temp.Path));

        var diagnostic = Assert.Single(ex.Diagnostics);
        Assert.Equal(DiagnosticCodes.TmdlUnresolvedReference, diagnostic.Code);
        Assert.Contains("FromColumn", diagnostic.Message);
    }

    [Fact]
    public void Content_hash_changes_when_any_file_changes()
    {
        using var temp = new TempDir(TestPaths.ExampleSalesSemanticModel);
        var source = ModelLocator.Resolve(temp.Path);
        var before = ModelLoader.ComputeContentHash(source);

        File.AppendAllText(temp.Combine("definition", "tables", "Store.tmdl"), "\n");

        Assert.NotEqual(before, ModelLoader.ComputeContentHash(source));
    }
}
