using TabularTools.Core.Queries;
using TabularTools.Core.Writes;

namespace TabularTools.Core.Tests;

public sealed class RecordingsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Keys_ignore_line_endings_and_trailing_whitespace_but_not_role()
    {
        var a = RecordingsFile.Key("EVALUATE\n\t{1}\n", null);
        Assert.Equal(a, RecordingsFile.Key("\r\nEVALUATE  \r\n\t{1}", null));
        Assert.NotEqual(a, RecordingsFile.Key("EVALUATE\n {1}", null));
        Assert.NotEqual(a, RecordingsFile.Key("EVALUATE\n\t{1}", "West"));
    }

    [Fact]
    public async Task Replays_results_caps_rows_and_fails_loudly_on_unknown_queries()
    {
        var executor = new RecordedDaxExecutor(MeasureTestRunnerTests.SalesRecordings);
        Assert.Equal(new DaxTarget("recorded:ExampleSales.recordings.yaml", "ExampleSales"), executor.Target);

        var file = RecordingsFile.Load(MeasureTestRunnerTests.SalesRecordings);
        var query = file.Recordings[0].Query!;
        var result = await executor.ExecuteAsync(new DaxQueryRequest(query, MaxRows: 2), Ct);
        Assert.True(result.Recorded);
        Assert.True(result.Truncated);
        Assert.Equal(2, result.RowCount);
        Assert.Equal([2023L, "East", 412345.50m, 0.31m], result.Rows[0]);

        var ex = await Assert.ThrowsAsync<DaxQueryException>(() => executor.ExecuteAsync(new DaxQueryRequest("EVALUATE {42}", Label: "x/head"), Ct));
        Assert.Contains("No recording for this query (x/head)", ex.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ArgumentException>(() => executor.ExecuteAsync(new DaxQueryRequest("{\"refresh\":{}}"), Ct));
        // The role is part of the match.
        await Assert.ThrowsAsync<DaxQueryException>(() => executor.ExecuteAsync(new DaxQueryRequest(query, Role: "West Region"), Ct));
    }

    [Fact]
    public async Task Recorded_errors_replay_as_query_errors()
    {
        using var temp = new TempDir();
        var path = temp.Combine("r.recordings.yaml");
        File.WriteAllText(path, "schemaVersion: 1\nrecordings:\n  - label: bad\n    query: EVALUATE {1}\n    error: \"Column 'X' not found\"\n");
        var ex = await Assert.ThrowsAsync<DaxQueryException>(() => new RecordedDaxExecutor(path).ExecuteAsync(new DaxQueryRequest("EVALUATE {1}"), Ct));
        Assert.Equal("Column 'X' not found", ex.Message);
    }

    [Theory]
    [InlineData("schemaVersion: 3", "schemaVersion 3")]
    [InlineData("recordings:\n  - label: a\n    columns: [{ name: x, dataType: int64 }]", "has no query")]
    [InlineData("recordings:\n  - label: a\n    query: EVALUATE {1}", "needs columns or an error")]
    [InlineData("recordings:\n  - label: a\n    query: EVALUATE {1}\n    columns: [{ name: x, dataType: int64 }]\n    rows: [[1, 2]]", "2 values for 1 columns")]
    [InlineData("recordings:\n  - label: a\n    query: EVALUATE {1}\n    columns: [{ name: x, dataType: int64 }]\n    rows: [[{ a: 1 }]]", "plain values")]
    public void Invalid_recordings_are_rejected(string yaml, string message)
    {
        using var temp = new TempDir();
        var path = temp.Combine("r.yaml");
        File.WriteAllText(path, yaml);
        var ex = Assert.Throws<ArgumentException>(() => RecordingsFile.Load(path));
        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recording_executor_saves_results_and_errors_merging_into_an_existing_file()
    {
        using var temp = new TempDir();
        var path = temp.Combine("live.recordings.yaml");
        var fake = new FakeExecutor();
        var recorder = new RecordingDaxExecutor(fake, () => new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero));
        await recorder.ExecuteAsync(new DaxQueryRequest("EVALUATE {1}", Label: "one"), Ct);
        await Assert.ThrowsAsync<DaxQueryException>(() => recorder.ExecuteAsync(new DaxQueryRequest("EVALUATE {2}", Label: "two"), Ct));
        var write = recorder.Save(path, new FileWriter([temp.Path]));
        Assert.Equal(WriteAction.Created, write.Files[0].Action);

        var replay = new RecordedDaxExecutor(path);
        Assert.Equal(new DaxTarget("recorded:live.recordings.yaml", "Sales"), replay.Target);
        var one = await replay.ExecuteAsync(new DaxQueryRequest("EVALUATE {1}"), Ct);
        Assert.Equal([1L, "a\"b", 2.5m, null, true], one.Rows[0]);
        Assert.Equal("boom", (await Assert.ThrowsAsync<DaxQueryException>(() => replay.ExecuteAsync(new DaxQueryRequest("EVALUATE {2}"), Ct))).Message);

        // Recording again replaces the same query instead of adding a duplicate.
        var again = new RecordingDaxExecutor(fake);
        await again.ExecuteAsync(new DaxQueryRequest("EVALUATE {1}", Label: "one"), Ct);
        again.Save(path, new FileWriter([temp.Path]));
        Assert.Equal(2, RecordingsFile.Load(path).Recordings.Count);
        Assert.Contains("recordedAt: \"2026-10-28T12:00:00Z\"", File.ReadAllText(path).Split("label: \"two\"")[1], StringComparison.Ordinal);
    }

    private sealed class FakeExecutor : IDaxExecutor
    {
        public DaxTarget Target { get; } = new("powerbi://api.powerbi.com/v1.0/myorg/WS", "Sales");

        public Task<DaxResult> ExecuteAsync(DaxQueryRequest request, CancellationToken cancellationToken = default)
        {
            if (request.Query.Contains("{2}", StringComparison.Ordinal)) throw new DaxQueryException("boom");
            return Task.FromResult(new DaxResult
            {
                Columns = [new("a", "int64"), new("b", "string"), new("c", "decimal"), new("d", "double"), new("e", "boolean")],
                Rows = [[1L, "a\"b", 2.5m, null, true]],
                Target = Target,
            });
        }
    }
}
