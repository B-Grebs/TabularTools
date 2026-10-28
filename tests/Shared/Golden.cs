namespace TabularTools.Tests.Shared;

/// <summary>
/// Golden-file comparison against <c>fixtures/diff/expected/</c>. Commit ids and the product version are replaced with
/// placeholders. Run with <c>UPDATE_GOLDEN=1</c> to rewrite the files after an intended change.
/// </summary>
internal static class Golden
{
    public static string Directory => Path.Combine(TestPaths.Fixtures, "diff", "expected");

    public static void Assert(string name, string actual, GitFixture? repo = null)
    {
        var normalized = Normalize(actual, repo);
        var path = Path.Combine(Directory, name);
        if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1")
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(path, normalized);
            return;
        }
        Xunit.Assert.True(File.Exists(path), $"Golden file missing: {path}. Run the tests with UPDATE_GOLDEN=1 to create it.");
        Xunit.Assert.Equal(File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal), normalized);
    }

    public static string Normalize(string text, GitFixture? repo)
    {
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (repo is not null)
        {
            text = text.Replace(repo.MainCommit, "{main}", StringComparison.Ordinal).Replace(repo.MainCommit[..7], "{main}", StringComparison.Ordinal);
            text = text.Replace(repo.FeatureCommit, "{feature}", StringComparison.Ordinal).Replace(repo.FeatureCommit[..7], "{feature}", StringComparison.Ordinal);
        }
        return text.Replace($"{TabularTools.Core.ProductInfo.Name} {TabularTools.Core.ProductInfo.Version}", "{product}", StringComparison.Ordinal);
    }
}
