using TabularTools.Core.Queries;
using TabularTools.Core.Testing;

namespace TabularTools.Core.Tests;

public sealed class MeasureTestFileTests
{
    [Fact]
    public void Loads_the_fixture_with_typed_snapshot_values()
    {
        var file = MeasureTestFile.Load(MeasureTestRunnerTests.SalesTests);
        Assert.Equal("../ExampleSales.SemanticModel", file.Model);
        Assert.Equal(TestPaths.ExampleSalesSemanticModel, file.ModelPath);
        Assert.Equal(["sales-by-year-region", "fy2023-total", "west-region-rls"], file.Cases.Select(c => c.Id));
        Assert.Equal(0.005, file.Defaults!.Tolerance!.Absolute);
        var snapshot = file.Cases[1].Assertions![0];
        Assert.Equal(1234567.00m, Assert.Single(snapshot.Rows!).Values[0]);
    }

    [Fact]
    public void Row_values_keep_their_yaml_types()
    {
        var file = MeasureTestFile.Parse("""
            model: m
            cases:
              - id: a
                measures: ["[X]"]
                assertions:
                  - type: snapshot
                    columns: ["'T'[A]", "'T'[B]", "'T'[C]", "'T'[D]", "'T'[E]", "'T'[F]", "[X]"]
                    rows:
                      - [2023, "2023", 1.5, 1e3, null, true, West]
            """, "test");
        var values = file.Cases[0].Assertions![0].Rows![0].Values;
        Assert.Equal([2023L, "2023", 1.5m, 1000.0, null, true, "West"], values);
    }

    [Theory]
    [InlineData("cases: []", "'model' is required")]
    [InlineData("schemaVersion: 2\nmodel: m", "unsupported schemaVersion 2")]
    [InlineData("model: m\ncases:\n  - measures: ['[X]']", "'id' is required")]
    [InlineData("model: m\ncases:\n  - id: 'bad id'\n    measures: ['[X]']", "'id' is required")]
    [InlineData("model: m\ncases:\n  - id: a\n    measures: ['[X]']\n  - id: A\n    measures: ['[X]']", "duplicate id")]
    [InlineData("model: m\ncases:\n  - id: a", "at least one measure")]
    [InlineData("model: m\ncases:\n  - id: a\n    measures: ['[X]']\n    assertions: [{ type: magic }]", "isn't differential, invariant or snapshot")]
    [InlineData("model: m\ncases:\n  - id: a\n    measures: ['[X]']\n    assertions: [{ type: invariant }]", "need 'expr'")]
    [InlineData("model: m\ncases:\n  - id: a\n    measures: ['[X]']\n    assertions: [{ type: snapshot, columns: ['[X]'] }]", "need 'rows'")]
    [InlineData("model: m\ncases:\n  - id: a\n    measures: ['[X]']\n    assertions: [{ type: snapshot, columns: ['[X]'], rows: [[1, 2]] }]", "2 values for 1 columns")]
    [InlineData("model: m\ncases:\n  - id: a\n    measures: ['[X]']\n    assertions: [{ type: differential, expr: '1' }]", "only applies to invariant")]
    [InlineData("model: m\ndefaults:\n  tolerance: { absolute: -1 }", "can't be negative")]
    [InlineData("model: m\nunknown: 1", "unknown")]
    [InlineData("model: [", "(1,")]
    public void Invalid_files_say_what_is_wrong(string yaml, string message)
    {
        var ex = Assert.Throws<TestFileException>(() => MeasureTestFile.Parse(yaml, "file.yaml"));
        Assert.Contains(message, ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("file.yaml", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Written_yaml_reads_back_the_same()
    {
        var original = MeasureTestFile.Load(MeasureTestRunnerTests.SalesTests);
        var yaml = original.ToYaml();
        var again = MeasureTestFile.Parse(yaml, "roundtrip");

        Assert.StartsWith("# yaml-language-server: $schema=" + MeasureTestFile.SchemaUrl, yaml, StringComparison.Ordinal);
        Assert.Equal(yaml, again.ToYaml());
        Assert.Equal(original.Cases.Count, again.Cases.Count);
        Assert.Equal("[Margin %] >= -1 && [Margin %] <= 1", again.Cases[0].Assertions![1].Expr);
        Assert.Equal("West Region", again.Cases[2].AsRole);
        Assert.Contains("          - [1234567.00]", yaml, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("2023")]
    [InlineData("true")]
    public void Ids_that_yaml_would_misread_are_quoted(string id)
    {
        var yaml = MeasureTestFile.CaseYaml(new TestCase { Id = id, Measures = ["[X]"] });
        Assert.Contains($"- id: \"{id}\"", yaml, StringComparison.Ordinal);
        Assert.Equal(id, MeasureTestFile.Parse("model: m\ncases:\n" + yaml, "x").Cases[0].Id);
    }

    [Fact]
    public void Shipped_schema_lists_every_property_of_the_file_types()
    {
        using var doc = System.Text.Json.JsonDocument.Parse(Schemas.MeasureTests);
        var root = doc.RootElement;
        Assert.Equal(File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "schemas", "measure-tests.schema.json")), Schemas.MeasureTests);

        static IEnumerable<string> Props(System.Text.Json.JsonElement e) => e.GetProperty("properties").EnumerateObject().Select(p => p.Name);
        static IEnumerable<string> Yaml(Type t) => t.GetProperties().Where(p => p.GetCustomAttributes(typeof(YamlDotNet.Serialization.YamlIgnoreAttribute), false).Length == 0)
            .Where(p => p.CanWrite).Select(p => char.ToLowerInvariant(p.Name[0]) + p.Name[1..]);

        Assert.Equal(Yaml(typeof(MeasureTestFile)).Order(), Props(root).Order());
        Assert.Equal(Yaml(typeof(TestDefaults)).Order(), Props(root.GetProperty("properties").GetProperty("defaults")).Order());
        Assert.Equal(Yaml(typeof(TestCase)).Order(), Props(root.GetProperty("$defs").GetProperty("case")).Order());
        Assert.Equal(Yaml(typeof(TestAssertion)).Order(), Props(root.GetProperty("$defs").GetProperty("assertion")).Order());
        Assert.Equal(Yaml(typeof(Config.ToleranceSettings)).Order(), Props(root.GetProperty("$defs").GetProperty("tolerance")).Order());
    }

    [Fact]
    public void Yaml_text_helpers_quote_and_format_values()
    {
        Assert.Equal("\"It's \\\"West\\\"\"", YamlText.Quote("It's \"West\""));
        Assert.Equal("[1, 2.5, \"x\", null, true, 3.0]", YamlText.Row([1L, 2.5m, "x", null, true, 3.0]));
        Assert.Equal("|-\n  a\n\n  b", YamlText.Block("a\n\nb", 2));
        Assert.Equal("\" a\"", YamlText.Block(" a", 2)); // leading indentation can't start a literal block
    }
}

public sealed class LiveCheckFixtureTests
{
    [Fact]
    public void LiveCheck_model_and_test_file_match_what_the_verification_script_expects()
    {
        var file = MeasureTestFile.Load(Path.Combine(TestPaths.Fixtures, "live", "livecheck.measuretests.yaml"));
        var model = Loading.ModelLoader.Load(file.ModelPath);
        Assert.Equal(["Base Amount", "Dependent", "Margin"], model.Snapshot.AllMeasures.Select(m => m.Name));
        Assert.Equal("Facts[Amount] * 2", model.Snapshot.AllColumns.Single(c => c.Name == "Double Amount").Expression);
        Assert.Equal("East Only", Assert.Single(model.Snapshot.Roles).Name);
        foreach (var c in file.Cases)
            Assert.Empty(CaseResolver.Resolve(c, file, model.Snapshot, model.Dependencies, null).Problems);
        // The script edits this exact text.
        Assert.Contains("measure 'Base Amount' = SUM(Facts[Amount])", File.ReadAllText(Path.Combine(TestPaths.Fixtures, "live", "LiveCheck.SemanticModel", "definition", "tables", "Facts.tmdl")), StringComparison.Ordinal);
    }
}
