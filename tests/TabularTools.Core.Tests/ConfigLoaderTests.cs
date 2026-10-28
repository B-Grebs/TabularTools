using TabularTools.Core.Config;

namespace TabularTools.Core.Tests;

public class ConfigLoaderTests
{
    [Fact]
    public void Discovers_the_nearest_config_above_a_folder_and_merges_the_local_file()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(temp.Combine("models", "Sales.SemanticModel"));
        File.WriteAllText(temp.Combine(ConfigLoader.FileName), """
            schemaVersion: 1
            lint:
              rulesets: [builtin, microsoft-bpa]
              failOn: error
              severity: { A: warning }
              suppress:
                - { rule: X, reason: shared }
            profiles:
              dev: { endpoint: "powerbi://api.powerbi.com/v1.0/myorg/Example", database: Sales }
            """);
        File.WriteAllText(temp.Combine(ConfigLoader.LocalFileName), """
            lint:
              failOn: warning
              severity: { B: off }
              suppress:
                - { rule: Y, reason: mine }
            """);

        var config = ConfigLoader.Discover(temp.Combine("models", "Sales.SemanticModel"));

        Assert.NotNull(config);
        Assert.Equal(temp.Combine(ConfigLoader.FileName), config.Path);
        Assert.Equal(["builtin", "microsoft-bpa"], config.Lint.Rulesets);
        Assert.Equal("warning", config.Lint.FailOn);
        Assert.Equal("warning", config.Lint.Severity!["A"]);
        Assert.Equal("off", config.Lint.Severity["B"]);
        Assert.Equal(["X", "Y"], config.Lint.Suppress!.Select(s => s.Rule));
    }

    [Fact]
    public void Discovery_stops_at_the_repo_root()
    {
        using var temp = new TempDir();
        File.WriteAllText(temp.Combine(ConfigLoader.FileName), "lint: {}");
        Directory.CreateDirectory(temp.Combine("repo", ".git"));
        Directory.CreateDirectory(temp.Combine("repo", "models"));

        Assert.Null(ConfigLoader.Discover(temp.Combine("repo", "models")));
    }

    [Theory]
    [InlineData("lint:\n  failon: error", "failon")]
    [InlineData("lint:\n  failOn: sometimes", "failOn")]
    [InlineData("lint:\n  severity: { A: loud }", "loud")]
    [InlineData("lint:\n  suppress:\n    - rule: A", "reason")]
    [InlineData("schemaVersion: 2", "schemaVersion")]
    [InlineData("lint: [", "tabulartools.yaml")]
    public void Invalid_configs_are_rejected_with_a_pointer_to_the_problem(string yaml, string expected)
    {
        using var temp = new TempDir();
        File.WriteAllText(temp.Combine(ConfigLoader.FileName), yaml);

        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Load(temp.Combine(ConfigLoader.FileName)));

        Assert.Contains(expected, ex.Message);
    }
}
