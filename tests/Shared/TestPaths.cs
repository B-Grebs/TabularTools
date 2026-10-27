namespace TabularTools.Tests.Shared;

/// <summary>Locates the repo's fixtures and creates throwaway copies for tests that modify files.</summary>
internal static class TestPaths
{
    public static string RepoRoot { get; } = FindRepoRoot();

    public static string Fixtures => Path.Combine(RepoRoot, "fixtures");

    public static string ExampleSalesFolder => Path.Combine(Fixtures, "pbip", "ExampleSales");

    public static string ExampleSalesPbip => Path.Combine(ExampleSalesFolder, "ExampleSales.pbip");

    public static string ExampleSalesSemanticModel => Path.Combine(ExampleSalesFolder, "ExampleSales.SemanticModel");

    public static string ExampleSalesDefinition => Path.Combine(ExampleSalesSemanticModel, "definition");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TabularTools.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not find the repo root (TabularTools.slnx) above " + AppContext.BaseDirectory);
    }
}

/// <summary>A temporary folder deleted on dispose. Optionally starts as a copy of a fixture folder.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir(string? copyFrom = null)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tabulartools-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
        if (copyFrom is not null) CopyDirectory(copyFrom, Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void CopyDirectory(string source, string target)
    {
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(System.IO.Path.Combine(target, System.IO.Path.GetRelativePath(source, dir)));
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, System.IO.Path.Combine(target, System.IO.Path.GetRelativePath(source, file)));
    }
}
