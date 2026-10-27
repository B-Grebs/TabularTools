using TabularTools.Core.Diagnostics;
using TabularTools.Core.Loading;

namespace TabularTools.Core.Tests;

public class ModelLocatorTests
{
    [Fact]
    public void Pbip_file_follows_the_report_to_its_semantic_model()
    {
        var source = ModelLocator.Resolve(TestPaths.ExampleSalesPbip);

        Assert.Equal(ModelSourceKind.Pbip, source.Kind);
        Assert.Equal(ModelFormat.Tmdl, source.Format);
        Assert.Equal(TestPaths.ExampleSalesDefinition, source.DefinitionPath);
        Assert.Equal(TestPaths.ExampleSalesSemanticModel, source.SemanticModelFolder);
        Assert.Equal("ExampleSales", source.Name);
    }

    [Fact]
    public void Semantic_model_folder_uses_its_definition_folder()
    {
        var source = ModelLocator.Resolve(TestPaths.ExampleSalesSemanticModel);

        Assert.Equal(ModelSourceKind.SemanticModelFolder, source.Kind);
        Assert.Equal(TestPaths.ExampleSalesDefinition, source.DefinitionPath);
    }

    [Fact]
    public void Definition_folder_takes_its_name_from_the_semantic_model_folder()
    {
        var source = ModelLocator.Resolve(TestPaths.ExampleSalesDefinition);

        Assert.Equal(ModelSourceKind.TmdlFolder, source.Kind);
        Assert.Equal("ExampleSales", source.Name);
    }

    [Fact]
    public void Folder_with_one_project_resolves_to_it()
    {
        var source = ModelLocator.Resolve(TestPaths.ExampleSalesFolder);

        Assert.Equal(ModelSourceKind.Pbip, source.Kind);
        Assert.Equal(TestPaths.ExampleSalesDefinition, source.DefinitionPath);
    }

    [Fact]
    public void Semantic_model_folder_without_tmdl_falls_back_to_model_bim()
    {
        using var temp = new TempDir();
        var folder = temp.Combine("Bim.SemanticModel");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "definition.pbism"), "{}");
        File.WriteAllText(Path.Combine(folder, "model.bim"), "{}");

        var source = ModelLocator.Resolve(folder);

        Assert.Equal(ModelFormat.Tmsl, source.Format);
        Assert.Equal(Path.Combine(folder, "model.bim"), source.DefinitionPath);
        Assert.Equal("Bim", source.Name);
    }

    [Fact]
    public void Missing_path_is_reported()
    {
        var ex = Assert.Throws<ModelLoadException>(() => ModelLocator.Resolve(Path.Combine(TestPaths.Fixtures, "does-not-exist")));

        Assert.Equal(DiagnosticCodes.PathNotFound, Assert.Single(ex.Diagnostics).Code);
        Assert.True(ex.IsResolutionFailure);
    }

    [Fact]
    public void Folder_that_is_not_a_model_is_reported()
    {
        using var temp = new TempDir();

        var ex = Assert.Throws<ModelLoadException>(() => ModelLocator.Resolve(temp.Path));

        Assert.Equal(DiagnosticCodes.NotAModel, Assert.Single(ex.Diagnostics).Code);
    }

    [Fact]
    public void Folder_with_two_models_lists_the_candidates()
    {
        using var temp = new TempDir();
        foreach (var name in new[] { "A.SemanticModel", "B.SemanticModel" })
        {
            Directory.CreateDirectory(temp.Combine(name));
            File.WriteAllText(temp.Combine(name, "definition.pbism"), "{}");
        }

        var ex = Assert.Throws<ModelLoadException>(() => ModelLocator.Resolve(temp.Path));

        var diagnostic = Assert.Single(ex.Diagnostics);
        Assert.Equal(DiagnosticCodes.AmbiguousModel, diagnostic.Code);
        Assert.Contains("A.SemanticModel", diagnostic.Message);
        Assert.Contains("B.SemanticModel", diagnostic.Message);
    }

    [Fact]
    public void Project_whose_report_uses_a_published_model_explains_what_to_pass()
    {
        using var temp = new TempDir();
        File.WriteAllText(temp.Combine("Thin.pbip"), """{ "version": "1.0", "artifacts": [ { "report": { "path": "Thin.Report" } } ] }""");
        Directory.CreateDirectory(temp.Combine("Thin.Report"));
        File.WriteAllText(temp.Combine("Thin.Report", "definition.pbir"),
            """{ "version": "4.0", "datasetReference": { "byConnection": { "connectionString": "Data Source=powerbi://api.powerbi.com/v1.0/myorg/Example;Initial Catalog=Sales" } } }""");

        var ex = Assert.Throws<ModelLoadException>(() => ModelLocator.Resolve(temp.Combine("Thin.pbip")));

        var diagnostic = Assert.Single(ex.Diagnostics);
        Assert.Equal(DiagnosticCodes.PbipUnresolved, diagnostic.Code);
        Assert.Contains("byConnection", diagnostic.Message);
    }
}
