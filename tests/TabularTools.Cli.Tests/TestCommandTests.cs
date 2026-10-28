using System.Text.Json;
using TabularTools.Core.Queries;

namespace TabularTools.Cli.Tests;

public sealed class TestCommandTests
{
    private static string FixtureTests => Path.Combine(TestPaths.ExampleSalesFolder, "tests");

    private static (int Exit, string Out, string Err) Run(string workingDirectory, IDaxExecutorFactory? factory, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var context = new CliContext(stdout, stderr, false, workingDirectory)
        {
            ExecutorFactory = factory ?? new NoLiveExecutors(),
            Clock = () => new DateTimeOffset(2026, 10, 28, 10, 0, 0, TimeSpan.Zero),
            Environment = _ => null,
        };
        var exit = CliApp.Run(args, context);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    /// <summary>Stands in for a live model by replaying the fixture recordings, and counts the queries it receives.</summary>
    private sealed class FakeLive(string? error = null, bool liveError = false) : IDaxExecutorFactory
    {
        public int Created { get; private set; }

        public IDaxExecutor Create(ResolvedProfile profile)
        {
            Created++;
            if (liveError) throw new LiveException("Couldn't get a token (default): no credentials.", "Run 'az login'.");
            return new Executor(new RecordedDaxExecutor(Path.Combine(FixtureTests, "ExampleSales.recordings.yaml")), profile.Target, error);
        }

        private sealed class Executor(RecordedDaxExecutor inner, DaxTarget target, string? error) : IDaxExecutor
        {
            public DaxTarget Target => target;

            public async Task<DaxResult> ExecuteAsync(DaxQueryRequest request, CancellationToken cancellationToken = default)
            {
                if (error is not null) throw new DaxQueryException(error);
                return (await inner.ExecuteAsync(request, cancellationToken)) with { Target = target, Recorded = false };
            }
        }
    }

    [Fact]
    public void Test_run_on_the_fixture_passes_with_json_output()
    {
        var (exit, output, _) = Run(TestPaths.ExampleSalesFolder, null, "test", "run", "tests/sales.measuretests.yaml");
        Assert.Equal(ExitCodes.Success, exit);
        using var doc = JsonDocument.Parse(output);
        Assert.Equal(4, doc.RootElement.GetProperty("summary").GetProperty("passed").GetInt32());
        Assert.False(doc.RootElement.GetProperty("blocking").GetBoolean());
        Assert.Equal("pass", doc.RootElement.GetProperty("files")[0].GetProperty("results")[0].GetProperty("status").GetString());
    }

    [Fact]
    public void Test_run_finds_files_from_config_and_prints_text()
    {
        var (exit, output, _) = Run(FixtureTests, null, "test", "run", "-f", "text");
        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("Measure tests (", output, StringComparison.Ordinal);
        Assert.Contains("PASS     fy2023-total [snapshot]", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Test_run_v1_to_v2_exits_1_on_a_regression_and_writes_markdown()
    {
        using var repo = DiffRepo();
        var (exit, output, _) = Run(repo.Path, null, "test", "run", "sales.measuretests.yaml", "--base", "main", "-f", "markdown");
        Assert.Equal(ExitCodes.BlockingFindings, exit);
        Assert.StartsWith("<!-- tabulartools:measure-tests -->", output, StringComparison.Ordinal);
        Assert.Contains("Changed without being edited: [Margin %]", output, StringComparison.Ordinal);

        var (onlyExpected, _, _) = Run(repo.Path, null, "test", "run", "sales.measuretests.yaml", "-b", "main", "--case", "total-sales-only", "units-by-year");
        Assert.Equal(ExitCodes.Success, onlyExpected);
        var (strict, _, _) = Run(repo.Path, null, "test", "run", "sales.measuretests.yaml", "-b", "main", "--case", "total-sales-only", "--strict");
        Assert.Equal(ExitCodes.BlockingFindings, strict);
    }

    [Fact]
    public void Test_run_reports_bad_files_and_missing_refs_with_exit_codes()
    {
        using var temp = new TempDir();
        File.WriteAllText(temp.Combine("bad.measuretests.yaml"), "cases: []\n");
        var (bad, _, err) = Run(temp.Path, null, "test", "run", "bad.measuretests.yaml");
        Assert.Equal(ExitCodes.UsageError, bad);
        Assert.Contains("'model' is required", err, StringComparison.Ordinal);

        using var repo = DiffRepo();
        var (env, _, gitErr) = Run(repo.Path, null, "test", "run", "sales.measuretests.yaml", "--base", "nope");
        Assert.Equal(ExitCodes.EnvironmentError, env);
        Assert.Contains("nope", gitErr, StringComparison.Ordinal);
    }

    [Fact]
    public void Record_captures_live_results_that_replay_offline()
    {
        using var temp = new TempDir(TestPaths.ExampleSalesFolder);
        var live = new FakeLive();
        var (exit, _, err) = Run(temp.Path, live, "test", "run", "tests/sales.measuretests.yaml", "-p", "dev", "--record", "tests/captured.recordings.yaml");
        Assert.Equal(ExitCodes.Success, exit);
        Assert.Equal(1, live.Created);
        Assert.Contains("Recorded 2 queries", err, StringComparison.Ordinal); // outside git the differentials are skipped: the invariant and the snapshot run
        var captured = RecordingsFile.Load(temp.Combine("tests", "captured.recordings.yaml"));
        Assert.Equal("powerbi://api.powerbi.com/v1.0/myorg/Example%20Sales%20Dev", captured.Target!.Endpoint);
        Assert.Equal(["fy2023-total", "sales-by-year-region/invariant-1"], captured.Recordings.Select(r => r.Label).Order(StringComparer.Ordinal));

        // Point the recorded profile at the capture and run offline.
        File.WriteAllText(temp.Combine("tests", "tabulartools.yaml"), File.ReadAllText(temp.Combine("tests", "tabulartools.yaml")).Replace("ExampleSales.recordings.yaml", "captured.recordings.yaml", StringComparison.Ordinal));
        var (offline, _, _) = Run(temp.Path, null, "test", "run", "tests/sales.measuretests.yaml", "--mode", "snapshot");
        Assert.Equal(ExitCodes.Success, offline);

        var (refused, _, refusal) = Run(temp.Path, null, "test", "run", "tests/sales.measuretests.yaml", "--record", "x.yaml");
        Assert.Equal(ExitCodes.UsageError, refused);
        Assert.Contains("--record needs a live profile", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void Live_auth_failures_exit_4()
    {
        using var temp = new TempDir(TestPaths.ExampleSalesFolder);
        var (exit, _, err) = Run(temp.Path, new FakeLive(liveError: true), "test", "run", "tests/sales.measuretests.yaml", "-p", "dev");
        Assert.Equal(ExitCodes.EnvironmentError, exit);
        Assert.Contains("az login", err, StringComparison.Ordinal);
    }

    [Fact]
    public void Test_gen_writes_a_case_and_dry_run_only_shows_it()
    {
        using var temp = new TempDir(TestPaths.ExampleSalesFolder);
        var (dry, dryOut, _) = Run(temp.Path, null, "test", "gen", "tests/new.measuretests.yaml", "--model", "ExampleSales.pbip",
            "--measures", "[Total Sales]", "[Total Cost]", "--group-by", "'Date'[Year]", "--dry-run", "-f", "text");
        Assert.Equal(ExitCodes.Success, dry);
        Assert.StartsWith("Would write", dryOut, StringComparison.Ordinal);
        Assert.Contains("+  - id: total-sales-total-cost-by-year", dryOut, StringComparison.Ordinal);
        Assert.False(File.Exists(temp.Combine("tests", "new.measuretests.yaml")));

        var (exit, output, _) = Run(temp.Path, null, "test", "gen", "tests/new.measuretests.yaml", "--model", "ExampleSales.pbip", "--measures", "[Total Sales]", "--case-id", "ts");
        Assert.Equal(ExitCodes.Success, exit);
        using var doc = JsonDocument.Parse(output);
        Assert.Equal("ts", doc.RootElement.GetProperty("caseId").GetString());
        Assert.Equal("created", doc.RootElement.GetProperty("write").GetProperty("files")[0].GetProperty("action").GetString());
        Assert.True(File.Exists(temp.Combine("tests", "new.measuretests.yaml")));

        var (badMode, _, modeErr) = Run(temp.Path, null, "test", "gen", "tests/new.measuretests.yaml", "--measures", "[Total Sales]", "--mode", "invariant");
        Assert.Equal(ExitCodes.UsageError, badMode);
        Assert.Contains("by hand", modeErr, StringComparison.Ordinal);
    }

    [Fact]
    public void Test_gen_snapshot_captures_through_the_live_profile()
    {
        using var temp = new TempDir(TestPaths.ExampleSalesFolder);
        var (exit, _, _) = Run(temp.Path, new FakeLive(), "test", "gen", "tests/sales.measuretests.yaml", "--measures", "[Total Sales]",
            "--filter", "'Date'[Year] = 2023", "--mode", "snapshot", "-p", "dev", "--case-id", "fy2023-live");
        Assert.Equal(ExitCodes.Success, exit);
        var text = File.ReadAllText(temp.Combine("tests", "sales.measuretests.yaml"));
        Assert.Contains("- id: fy2023-live", text, StringComparison.Ordinal);
        Assert.Contains("capturedAt: \"2026-10-28T10:00:00Z\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Review_includes_measure_tests_unless_told_not_to()
    {
        using var repo = DiffRepo();
        var (exit, output, _) = Run(repo.Path, null, "review", "--base", "main", "--no-lint", "-f", "markdown", "--json-out", "out/review.json");
        Assert.Equal(ExitCodes.BlockingFindings, exit);
        Assert.Contains("#### ❌ Measure tests", output, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(File.ReadAllText(repo.Combine("out", "review.json")));
        Assert.Equal(1, json.RootElement.GetProperty("tests").GetProperty("summary").GetProperty("failed").GetInt32());

        var (_, without, _) = Run(repo.Path, null, "review", "--base", "main", "--no-lint", "--no-tests", "-f", "markdown");
        Assert.DoesNotContain("Measure tests", without, StringComparison.Ordinal);
    }

    private static GitFixture DiffRepo()
    {
        var repo = new GitFixture();
        foreach (var f in Directory.GetFiles(Path.Combine(TestPaths.Fixtures, "diff", "tests"))) File.Copy(f, repo.Combine(Path.GetFileName(f)));
        return repo;
    }
}

public sealed class DaxCommandTests
{
    private static (int Exit, string Out, string Err) Run(IDaxExecutorFactory factory, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = CliApp.Run(args, new CliContext(stdout, stderr, false, TestPaths.RepoRoot) { ExecutorFactory = factory, Environment = _ => null });
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private static string Config => Path.Combine(TestPaths.ExampleSalesFolder, "tests", "tabulartools.yaml");

    private sealed class Fake(Func<DaxQueryRequest, DaxResult>? answer = null, Exception? error = null) : IDaxExecutorFactory, IDaxExecutor
    {
        public List<DaxQueryRequest> Requests { get; } = [];
        public DaxTarget Target { get; } = new("powerbi://api.powerbi.com/v1.0/myorg/Example%20Sales%20Dev", "ExampleSales");
        public IDaxExecutor Create(ResolvedProfile profile) => profile.IsRecorded ? new RecordedDaxExecutor(profile.RecordingsPath!) : this;

        public Task<DaxResult> ExecuteAsync(DaxQueryRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (error is not null) throw error;
            return Task.FromResult(answer!(request));
        }
    }

    [Fact]
    public void Dax_prints_rows_as_json_text_or_markdown()
    {
        var fake = new Fake(_ => new DaxResult { Columns = [new("[Region]", "string"), new("[Sales]", "decimal")], Rows = [["West", 12.5m]], Target = new("x", "ExampleSales"), DurationMs = 7 });
        var (exit, json, _) = Run(fake, "dax", "-c", Config, "-p", "dev", "-q", "EVALUATE ROW(\"Sales\", [Total Sales])", "--max-rows", "10", "--role", "West Region");
        Assert.Equal(ExitCodes.Success, exit);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(12.5m, doc.RootElement.GetProperty("rows")[0][1].GetDecimal());
        var request = Assert.Single(fake.Requests);
        Assert.Equal((10, "West Region"), (request.MaxRows, request.Role));

        var (_, text, _) = Run(fake, "dax", "-c", Config, "-p", "dev", "-q", "EVALUATE {1}", "-f", "text");
        Assert.Contains("[Region]  [Sales]", text, StringComparison.Ordinal);
        Assert.Contains("1 row from ExampleSales in 7 ms.", text, StringComparison.Ordinal);
        var (_, md, _) = Run(fake, "dax", "-c", Config, "-p", "dev", "-q", "EVALUATE {1}", "-f", "markdown");
        Assert.Contains("| West | 12.5 |", md, StringComparison.Ordinal);
    }

    [Fact]
    public void Dax_reads_queries_from_files_and_replays_recorded_profiles()
    {
        using var temp = new TempDir();
        var query = RecordingsFile.Load(Path.Combine(TestPaths.ExampleSalesFolder, "tests", "ExampleSales.recordings.yaml")).Recordings.Single(r => r.Label == "fy2023-total").Query!;
        File.WriteAllText(temp.Combine("q.dax"), query);
        var (exit, output, _) = Run(new Fake(), "dax", "-c", Config, "--file", temp.Combine("q.dax"));
        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("\"recorded\": true", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{ \"refresh\": {} }", "XMLA and TMSL")]
    [InlineData("", "--query or --file")]
    public void Dax_rejects_non_queries_before_connecting(string query, string message)
    {
        var fake = new Fake();
        var (exit, _, err) = Run(fake, "dax", "-c", Config, "-p", "dev", "-q", query);
        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.Contains(message, err, StringComparison.Ordinal);
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public void Dax_maps_query_and_connection_errors_to_exit_codes_and_honours_allow_run_dax()
    {
        var (query, _, queryErr) = Run(new Fake(error: new DaxQueryException("Column 'Nope' not found.")), "dax", "-c", Config, "-p", "dev", "-q", "EVALUATE {1}");
        Assert.Equal(ExitCodes.UsageError, query);
        Assert.Contains("the query failed: Column 'Nope' not found.", queryErr, StringComparison.Ordinal);

        var (live, _, _) = Run(new Fake(error: new LiveException("no route")), "dax", "-c", Config, "-p", "dev", "-q", "EVALUATE {1}");
        Assert.Equal(ExitCodes.EnvironmentError, live);

        using var temp = new TempDir();
        File.WriteAllText(temp.Combine("tabulartools.yaml"), File.ReadAllText(Config) + "live:\n  allowRunDax: false\n");
        var fake = new Fake();
        var (off, _, offErr) = Run(fake, "dax", "-c", temp.Combine("tabulartools.yaml"), "-p", "dev", "-q", "EVALUATE {1}");
        Assert.Equal(ExitCodes.UsageError, off);
        Assert.Contains("allowRunDax: false", offErr, StringComparison.Ordinal);
        Assert.Empty(fake.Requests);
    }
}
