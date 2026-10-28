namespace TabularTools.Core.Tests;

/// <summary>Copies the ExampleSales semantic model to a temp folder and applies text edits to its TMDL files.</summary>
internal sealed class EditedModel : IDisposable
{
    private readonly TempDir _temp = new(TestPaths.ExampleSalesSemanticModel);

    public string Path => _temp.Path;

    public string File(params string[] relative) => _temp.Combine(["definition", .. relative]);

    public EditedModel Replace(string relativeFile, string find, string replace)
    {
        var file = File(relativeFile.Split('/'));
        var text = System.IO.File.ReadAllText(file);
        Assert.Contains(find, text);
        System.IO.File.WriteAllText(file, text.Replace(find, replace));
        return this;
    }

    public EditedModel Append(string relativeFile, string text)
    {
        System.IO.File.AppendAllText(File(relativeFile.Split('/')), text);
        return this;
    }

    public void Dispose() => _temp.Dispose();
}
