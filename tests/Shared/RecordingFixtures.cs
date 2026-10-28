using TabularTools.Core.Queries;
using TabularTools.Core.Testing;

namespace TabularTools.Tests.Shared;

/// <summary>
/// Keeps hand-written recordings in step with the query planner. The values are written by hand; the query text is the
/// planner's. Running with <c>UPDATE_GOLDEN=1</c> rewrites each recording's query (matched by label), keeping its result.
/// </summary>
internal static class RecordingFixtures
{
    /// <summary>Labels whose recorded query doesn't match what the runner sends now (after updating, when UPDATE_GOLDEN=1).</summary>
    public static async Task<IReadOnlyList<string>> CheckAsync(TestRunRequest request, string recordingsPath)
    {
        var captured = new List<(string Label, string Query, string? Role)>();
        await MeasureTestRunner.RunAsync(request with { WrapExecutor = inner => new Capturing(inner.Target, captured) }, new NoLiveExecutors());

        var file = RecordingsFile.Load(recordingsPath);
        var problems = new List<string>();
        var changed = false;
        foreach (var (label, query, role) in captured)
        {
            var recording = file.Recordings.FirstOrDefault(r => r.Label == label);
            if (recording is null) { problems.Add($"{label}: no recording with this label"); continue; }
            if (RecordingsFile.Key(recording.Query!, recording.Role) == RecordingsFile.Key(query, role)) continue;
            if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1")
            {
                recording.Query = RecordingsFile.Canonical(query);
                recording.Role = role;
                changed = true;
            }
            else problems.Add($"{label}: recorded query differs from the planner's (run with UPDATE_GOLDEN=1 to update)");
        }
        if (changed) File.WriteAllText(recordingsPath, file.ToYaml());
        return problems;
    }

    private sealed class Capturing(DaxTarget target, List<(string, string, string?)> captured) : IDaxExecutor
    {
        public DaxTarget Target => target;

        public Task<DaxResult> ExecuteAsync(DaxQueryRequest request, CancellationToken cancellationToken = default)
        {
            lock (captured) captured.Add((request.Label ?? "?", request.Query, request.Role));
            throw new DaxQueryException("captured");
        }
    }
}

/// <summary>An executor factory for tests that must never reach a live model.</summary>
internal sealed class NoLiveExecutors : IDaxExecutorFactory
{
    public IDaxExecutor Create(ResolvedProfile profile) => throw new InvalidOperationException($"Test tried to connect to live profile '{profile.Name}'.");
}
