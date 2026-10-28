using TabularTools.Core.Diff;
using TabularTools.Core.Queries;
using TabularTools.Core.Testing;

namespace TabularTools.Core.Tests;

public sealed class MeasureTestRunnerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static string SalesTests => Path.Combine(TestPaths.ExampleSalesFolder, "tests", "sales.measuretests.yaml");
    public static string SalesRecordings => Path.Combine(TestPaths.ExampleSalesFolder, "tests", "ExampleSales.recordings.yaml");
    public static string DiffTestsFolder => Path.Combine(TestPaths.Fixtures, "diff", "tests");

    private static TestRunRequest FixtureRequest() => new() { TestFiles = [SalesTests], WorkingDirectory = TestPaths.ExampleSalesFolder };

    /// <summary>The v1 → v2 git repo with the diff test files copied into its working tree (untracked).</summary>
    internal static GitFixture DiffRepo()
    {
        var repo = new GitFixture();
        foreach (var f in Directory.GetFiles(DiffTestsFolder)) File.Copy(f, repo.Combine(Path.GetFileName(f)));
        return repo;
    }

    private static TestRunRequest DiffRequest(GitFixture repo) => new()
    {
        TestFiles = [repo.Combine("sales.measuretests.yaml")],
        Base = "main",
        WorkingDirectory = repo.Path,
    };

    [Fact]
    public async Task Fixture_recordings_match_the_planner()
    {
        Assert.Empty(await RecordingFixtures.CheckAsync(FixtureRequest(), SalesRecordings));
        using var repo = DiffRepo();
        Assert.Empty(await RecordingFixtures.CheckAsync(DiffRequest(repo), Path.Combine(DiffTestsFolder, "ExampleSales-v1-v2.recordings.yaml")));
    }

    [Fact]
    public async Task Unchanged_model_passes_every_assertion_with_one_query_per_case()
    {
        var result = await MeasureTestRunner.RunAsync(FixtureRequest(), new NoLiveExecutors(), Ct);

        Assert.False(result.Blocking);
        Assert.Equal(new TestSummary(4, 0, 0, 0, 0), result.Summary);
        Assert.Equal(["recorded"], result.Profiles);
        Assert.Equal("ExampleSales", Assert.Single(result.Targets).Database);
        // Nothing changed against HEAD, so base and head are the same query: 3 values queries + 1 invariant.
        Assert.Equal(4, result.QueriesRun);
        var file = Assert.Single(result.Files);
        Assert.Equal("tests/sales.measuretests.yaml", file.File);
        Assert.Empty(file.StructuralChangesNotTested);
        var invariant = file.Results.Single(r => r.Assertion == "invariant");
        Assert.Equal(0, invariant.Violations);
        Assert.Equal("West Region", file.Results.Single(r => r.CaseId == "west-region-rls").Role);
    }

    [Fact]
    public async Task Differential_v1_to_v2_separates_expected_changes_from_regressions()
    {
        using var repo = DiffRepo();
        var result = await MeasureTestRunner.RunAsync(DiffRequest(repo), new NoLiveExecutors(), Ct);

        Assert.True(result.Blocking);
        Assert.Equal(new TestSummary(2, 1, 1, 0, 0), result.Summary);
        Assert.Equal(DiffSideKind.Worktree, result.Head!.Kind);
        Assert.Equal(repo.MainCommit, result.Base!.Commit);
        var file = Assert.Single(result.Files);
        var byCase = file.Results.ToDictionary(r => r.CaseId);

        var sales = byCase["sales-by-year"];
        Assert.Equal(TestStatus.Fail, sales.Status);
        Assert.Equal([new MeasureOutcome("Total Sales", MeasureChange.ExpectedChange, 2), new MeasureOutcome("Margin %", MeasureChange.Regression, 2)], sales.Measures);
        Assert.Equal(4, sales.DiffCount);
        Assert.False(sales.Diffs![0].Expected); // regressions listed first
        Assert.Equal("Margin %", sales.Diffs[0].Measure);
        Assert.Contains("[Margin %] (2 rows)", sales.Message, StringComparison.Ordinal);

        var total = byCase["total-sales-only"];
        Assert.Equal(TestStatus.Changed, total.Status);
        Assert.True(total.ExpectedChange);
        var first = total.Diffs![0];
        Assert.Equal(2023L, first.Key["Date[Year]"]);
        Assert.Equal(523456.25m, first.Base);
        Assert.Equal(525800.00m, first.Head);
        Assert.Equal(2343.75, first.Delta!.Value, 6);

        Assert.Equal(TestStatus.Pass, byCase["units-by-year"].Status);     // renamed, same value
        Assert.Equal(TestStatus.Pass, byCase["customers-by-year"].Status); // moved, same value

        Assert.Equal(["'Sales'[Total Sales]"], file.Overrides!.DirectlyEdited);
        Assert.Equal(["'Sales'[Total Sales]", "'Sales'[Total Quantity]", "'Sales'[Average Price]"], file.Overrides.Base);
        Assert.Equal(["'Sales'[Total Sales]", "'Sales'[Units Sold]", "'Promotion'[Promotion Count]"], file.Overrides.Head);

        var structural = file.StructuralChangesNotTested.Select(c => c.Kind).ToHashSet();
        Assert.Contains("relationship.crossFilterChanged", structural);
        Assert.Contains("relationship.cardinalityChanged", structural);
        Assert.Contains("role.rlsFilterChanged", structural);
        Assert.Contains("column.dataTypeChanged", structural);
        Assert.Contains("column.renamed", structural);
        Assert.DoesNotContain("measure.expressionChanged", structural);
        Assert.DoesNotContain("column.summarizeByChanged", structural);
    }

    [Fact]
    public async Task Strict_makes_expected_changes_block_and_mode_and_case_filters_narrow_the_run()
    {
        using var repo = DiffRepo();
        var strict = await MeasureTestRunner.RunAsync(DiffRequest(repo) with { Strict = true, CaseIds = ["total-sales-only"] }, new NoLiveExecutors(), Ct);
        Assert.True(strict.Blocking);
        Assert.Equal(new TestSummary(0, 0, 1, 0, 0), strict.Summary);

        var lenient = await MeasureTestRunner.RunAsync(DiffRequest(repo) with { CaseIds = ["total-sales-only"] }, new NoLiveExecutors(), Ct);
        Assert.False(lenient.Blocking);

        var snapshotOnly = await MeasureTestRunner.RunAsync(FixtureRequest() with { Mode = AssertionKind.Snapshot }, new NoLiveExecutors(), Ct);
        Assert.Equal(["snapshot", "invariant"], snapshotOnly.Files[0].Results.Select(r => r.Assertion).Order().Reverse());
    }

    [Fact]
    public async Task Head_as_a_ref_reads_the_model_from_git()
    {
        using var repo = DiffRepo();
        var result = await MeasureTestRunner.RunAsync(DiffRequest(repo) with { Head = "feature" }, new NoLiveExecutors(), Ct);
        Assert.Equal(repo.FeatureCommit, result.Head!.Commit);
        Assert.Equal(new TestSummary(2, 1, 1, 0, 0), result.Summary);
    }

    [Fact]
    public async Task Outside_git_differential_assertions_are_skipped_and_the_rest_runs()
    {
        using var temp = new TempDir(TestPaths.ExampleSalesFolder);
        var result = await MeasureTestRunner.RunAsync(new TestRunRequest { TestFiles = [temp.Combine("tests", "sales.measuretests.yaml")], WorkingDirectory = temp.Path }, new NoLiveExecutors(), Ct);

        var file = Assert.Single(result.Files);
        Assert.Equal("TEST001", Assert.Single(file.Diagnostics!).Code);
        Assert.Null(file.Overrides);
        Assert.Equal(2, file.Results.Count(r => r.Status == TestStatus.Skipped));
        Assert.Equal(2, file.Results.Count(r => r.Status == TestStatus.Pass)); // the invariant and the snapshot
        Assert.False(result.Blocking);
    }

    [Fact]
    public async Task Unknown_measures_unrecorded_queries_and_failed_snapshots_are_reported_per_assertion()
    {
        using var temp = new TempDir(TestPaths.ExampleSalesFolder);
        var path = temp.Combine("tests", "sales.measuretests.yaml");
        var yaml = File.ReadAllText(path)
            .Replace("- [1234567.00]", "- [1234000.00]", StringComparison.Ordinal)
            + """
              - id: typo
                measures: ["[Totl Sales]"]
              - id: not-recorded
                measures: ["[Total Cost]"]

            """;
        File.WriteAllText(path, yaml);

        var result = await MeasureTestRunner.RunAsync(new TestRunRequest { TestFiles = [path], WorkingDirectory = temp.Path }, new NoLiveExecutors(), Ct);
        var byCase = result.Files[0].Results.GroupBy(r => r.CaseId).ToDictionary(g => g.Key, g => g.ToList());

        var typo = Assert.Single(byCase["typo"]);
        Assert.Equal(TestStatus.Error, typo.Status);
        Assert.Contains("[Totl Sales]", typo.Message, StringComparison.Ordinal);

        var notRecorded = Assert.Single(byCase["not-recorded"]);
        Assert.Equal(TestStatus.Skipped, notRecorded.Status); // outside git: no base, so the differential is skipped before querying

        var snapshot = Assert.Single(byCase["fy2023-total"]);
        Assert.Equal(TestStatus.Fail, snapshot.Status);
        var diff = Assert.Single(snapshot.Diffs!);
        Assert.Equal(1234000.00m, diff.Base);
        Assert.Equal(1234567.00m, diff.Head);
        Assert.True(result.Blocking);
    }

    [Fact]
    public async Task Unrecorded_query_is_an_error_naming_the_case()
    {
        using var repo = DiffRepo();
        File.AppendAllText(repo.Combine("sales.measuretests.yaml"), "  - id: cost\n    measures: [\"[Total Cost]\"]\n");
        var result = await MeasureTestRunner.RunAsync(DiffRequest(repo) with { CaseIds = ["cost"] }, new NoLiveExecutors(), Ct);
        var r = Assert.Single(result.Files[0].Results);
        Assert.Equal(TestStatus.Error, r.Status);
        Assert.Contains("No recording for this query (cost)", r.Message, StringComparison.Ordinal);
        Assert.Contains("--record", r.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Live_profiles_are_refused_when_live_access_is_off()
    {
        using var temp = new TempDir(TestPaths.ExampleSalesFolder);
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => MeasureTestRunner.RunAsync(new TestRunRequest
        {
            TestFiles = [temp.Combine("tests", "sales.measuretests.yaml")],
            Profile = "dev",
            AllowLive = false,
            WorkingDirectory = temp.Path,
        }, new NoLiveExecutors(), Ct));
        Assert.Contains("--disable-live", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Test_files_come_from_tests_files_when_none_are_given()
    {
        var result = await MeasureTestRunner.RunAsync(new TestRunRequest { WorkingDirectory = Path.Combine(TestPaths.ExampleSalesFolder, "tests") }, new NoLiveExecutors(), Ct);
        Assert.Equal("sales.measuretests.yaml", Assert.Single(result.Files).File);
    }
}
