using TabularTools.Core.Testing;
using TabularTools.Core.Writes;

namespace TabularTools.Core.Tests;

public sealed class MeasureTestGeneratorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 9, 30, 0, TimeSpan.Zero);

    private static Task<GenerateResult> Generate(TempDir temp, GenerateRequest request) =>
        MeasureTestGenerator.GenerateAsync(request, new FileWriter([temp.Path]), new NoLiveExecutors(), () => Now, Ct);

    [Fact]
    public async Task Creates_a_differential_case_with_canonical_names_and_a_default_id()
    {
        using var temp = new TempDir(TestPaths.ExampleSalesFolder);
        var result = await Generate(temp, new GenerateRequest
        {
            TestFile = "tests/new.measuretests.yaml",
            Model = "ExampleSales.SemanticModel",
            Measures = ["total sales", "Sales[margin %]"],
            GroupBy = ["Date[Year]"],
            Filters = ["'Date'[Year] = 2024"],
            Profile = "recorded",
            Save = true,
            WorkingDirectory = temp.Path,
        });

        Assert.Equal("total-sales-margin-by-year", result.CaseId);
        Assert.False(result.Replaced);
        Assert.Equal(WriteAction.Created, result.Write.Files[0].Action);
        Assert.Equal(result.Yaml, File.ReadAllText(temp.Combine("tests", "new.measuretests.yaml")));
        Assert.Equal($"""
            # yaml-language-server: $schema={MeasureTestFile.SchemaUrl}
            schemaVersion: 1
            model: "../ExampleSales.SemanticModel"
            profile: "recorded"
            cases:
              - id: total-sales-margin-by-year
                measures: ["[Total Sales]", "[Margin %]"]
                groupBy: ["'Date'[Year]"]
                filters: ["'Date'[Year] = 2024"]
                assertions:
                  - type: differential

            """.Replace("\r\n", "\n", StringComparison.Ordinal), result.Yaml);
    }

    [Fact]
    public async Task Dry_run_is_the_default_and_writes_nothing()
    {
        using var temp = new TempDir(TestPaths.ExampleSalesFolder);
        var result = await Generate(temp, new GenerateRequest { TestFile = "x.measuretests.yaml", Model = "ExampleSales.pbip", Measures = ["[Total Cost]"], WorkingDirectory = temp.Path });
        Assert.True(result.Write.DryRun);
        Assert.False(File.Exists(temp.Combine("x.measuretests.yaml")));
        Assert.Contains("model: \"ExampleSales.pbip\"", result.Yaml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Appending_keeps_comments_and_replacing_rewrites_with_a_warning()
    {
        using var temp = new TempDir(TestPaths.ExampleSalesFolder);
        var path = temp.Combine("tests", "sales.measuretests.yaml");
        var before = File.ReadAllText(path);

        var appended = await Generate(temp, new GenerateRequest { TestFile = path, Measures = ["[Total Cost]"], CaseId = "cost", Save = true, WorkingDirectory = temp.Path });
        Assert.Equal(["sales-by-year-region", "fy2023-total", "west-region-rls", "cost"], appended.Cases);
        var after = File.ReadAllText(path);
        Assert.StartsWith(before.TrimEnd('\n'), after, StringComparison.Ordinal); // existing text, comments included, untouched
        Assert.EndsWith("  - id: cost\n    measures: [\"[Total Cost]\"]\n    assertions:\n      - type: differential\n", after, StringComparison.Ordinal);
        Assert.Null(appended.Diagnostics);

        var replaced = await Generate(temp, new GenerateRequest { TestFile = path, Measures = ["[Total Quantity]"], CaseId = "COST", Save = true, WorkingDirectory = temp.Path });
        Assert.True(replaced.Replaced);
        Assert.Equal(4, replaced.Cases.Count);
        Assert.Equal("TEST010", Assert.Single(replaced.Diagnostics!).Code);
        Assert.Contains("[Total Quantity]", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.DoesNotContain("pin closed periods", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Snapshot_mode_captures_values_through_the_profile()
    {
        using var temp = new TempDir(TestPaths.ExampleSalesFolder);
        var result = await Generate(temp, new GenerateRequest
        {
            TestFile = "tests/sales.measuretests.yaml",
            Measures = ["[Total Sales]"],
            Filters = ["'Date'[Year] = 2023"],
            Mode = AssertionKind.Snapshot,
            CaseId = "fy2023-again",
            Save = true,
            WorkingDirectory = temp.Path,
        });
        Assert.Contains("""
              - id: fy2023-again
                measures: ["[Total Sales]"]
                filters: ["'Date'[Year] = 2023"]
                assertions:
                  - type: snapshot
                    capturedAt: "2026-10-28T09:30:00Z"
                    columns: ["[Total Sales]"]
                    rows:
                      - [1234567.00]
            """.Replace("\r\n", "\n", StringComparison.Ordinal), result.Yaml, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("tests/x.yaml", "[Total Sales]", "must end with .measuretests.yaml")]
    [InlineData("tests/x.measuretests.yaml", "[Nope]", "[Nope]")]
    [InlineData("tests/x.measuretests.yaml", "Sales[Amount]", "not a measure")]
    public async Task Bad_requests_fail_before_writing(string file, string measure, string message)
    {
        using var temp = new TempDir(TestPaths.ExampleSalesFolder);
        var ex = await Assert.ThrowsAnyAsync<ArgumentException>(() => Generate(temp, new GenerateRequest { TestFile = file, Model = "ExampleSales.pbip", Measures = [measure], Save = true, WorkingDirectory = temp.Path }));
        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(temp.Combine(file)));
    }

    [Fact]
    public async Task Needs_a_model_and_refuses_files_outside_the_roots()
    {
        using var temp = new TempDir(TestPaths.ExampleSalesFolder);
        await Assert.ThrowsAsync<ArgumentException>(() => Generate(temp, new GenerateRequest { TestFile = "n.measuretests.yaml", Measures = ["[Total Sales]"], WorkingDirectory = temp.Path }));
        using var other = new TempDir();
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => Generate(temp, new GenerateRequest
        {
            TestFile = other.Combine("n.measuretests.yaml"), Model = temp.Combine("ExampleSales.pbip"), Measures = ["[Total Sales]"], Save = true, WorkingDirectory = temp.Path,
        }));
        Assert.Contains("outside the allowed folders", ex.Message, StringComparison.Ordinal);
    }
}
