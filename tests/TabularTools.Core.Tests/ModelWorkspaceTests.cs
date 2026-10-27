using TabularTools.Core.Loading;

namespace TabularTools.Core.Tests;

public class ModelWorkspaceTests
{
    [Fact]
    public void Second_open_of_an_unchanged_model_comes_from_cache()
    {
        var workspace = new ModelWorkspace([TestPaths.ExampleSalesFolder]);

        var first = workspace.Open("ExampleSales.pbip");
        var second = workspace.Open(first.Handle);

        Assert.False(first.FromCache);
        Assert.True(second.FromCache);
        Assert.Same(first.Model, second.Model);
        Assert.StartsWith(ModelWorkspace.HandlePrefix, first.Handle);
    }

    [Fact]
    public void Same_model_by_different_paths_gets_the_same_handle()
    {
        var workspace = new ModelWorkspace([TestPaths.ExampleSalesFolder]);

        var byProject = workspace.Open("ExampleSales.pbip");
        var byFolder = workspace.Open(TestPaths.ExampleSalesSemanticModel);

        Assert.Equal(byProject.Handle, byFolder.Handle);
        Assert.True(byFolder.FromCache);
    }

    [Fact]
    public void Edited_model_is_reloaded()
    {
        using var temp = new TempDir(TestPaths.ExampleSalesSemanticModel);
        var workspace = new ModelWorkspace([temp.Path]);
        var first = workspace.Open(null);

        var sales = temp.Combine("definition", "tables", "Sales.tmdl");
        File.WriteAllText(sales, File.ReadAllText(sales).Replace("measure 'Total Quantity'", "measure 'Units Sold'"));
        var second = workspace.Open(first.Handle);

        Assert.False(second.FromCache);
        Assert.Contains(second.Model.Snapshot.AllMeasures, m => m.Name == "Units Sold");
    }

    [Fact]
    public void Unknown_handle_is_reported()
    {
        var workspace = new ModelWorkspace([TestPaths.ExampleSalesFolder]);

        var ex = Assert.Throws<ModelLoadException>(() => workspace.Open("m-0000000000"));

        Assert.Contains("Unknown model handle", ex.Message);
    }
}
