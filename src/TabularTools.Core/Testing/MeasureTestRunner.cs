using System.Collections.Concurrent;
using TabularTools.Core.Config;
using TabularTools.Core.Dax;
using TabularTools.Core.Diagnostics;
using TabularTools.Core.Diff;
using TabularTools.Core.Git;
using TabularTools.Core.Lint;
using TabularTools.Core.Loading;
using TabularTools.Core.Queries;
using TabularTools.Core.Snapshots;

namespace TabularTools.Core.Testing;

public sealed record TestRunRequest
{
    /// <summary>Test files. Empty: the files matched by <c>tests.files</c> in the config.</summary>
    public IReadOnlyList<string> TestFiles { get; init; } = [];
    /// <summary>Connection profile; otherwise TABULARTOOLS_PROFILE, the test file's, or tests.profile.</summary>
    public string? Profile { get; init; }
    /// <summary>Base git ref for differential tests. Default: HEAD (so the working tree's edits are tested).</summary>
    public string? Base { get; init; }
    /// <summary>Head git ref, or WORKTREE (default) for the files on disk.</summary>
    public string? Head { get; init; }
    public bool MergeBase { get; init; } = true;
    /// <summary>Only run assertions of this kind (invariants run with either).</summary>
    public AssertionKind? Mode { get; init; }
    public IReadOnlyList<string>? CaseIds { get; init; }
    public bool? Strict { get; init; }
    public string? ConfigPath { get; init; }
    public required string WorkingDirectory { get; init; }
    public Func<string, string?>? Environment { get; init; }
    /// <summary>False refuses live profiles (MCP --disable-live); recordings still work.</summary>
    public bool AllowLive { get; init; } = true;
    /// <summary>Wraps each executor, e.g. to record results.</summary>
    public Func<IDaxExecutor, IDaxExecutor>? WrapExecutor { get; init; }
}

/// <summary>Runs measure regression tests (ARCHITECTURE §8, <c>run_measure_tests</c>).</summary>
public static class MeasureTestRunner
{
    public const int MaxDiffsListed = 20;
    public const int MaxRows = 10_000;
    public const int MaxViolatingRowsListed = 10;

    public static async Task<MeasureTestRunResult> RunAsync(TestRunRequest request, IDaxExecutorFactory factory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(factory);
        var cwd = Path.GetFullPath(request.WorkingDirectory);
        var config = request.ConfigPath is { Length: > 0 } cp
            ? ConfigLoader.Load(Path.GetFullPath(Path.Combine(cwd, cp)))
            : ConfigLoader.Discover(request.TestFiles.Count > 0 ? Path.GetDirectoryName(Path.GetFullPath(Path.Combine(cwd, request.TestFiles[0])))! : cwd);
        var paths = request.TestFiles.Count > 0
            ? request.TestFiles.Select(f => Path.GetFullPath(Path.Combine(cwd, f))).ToList()
            : FindTestFiles(config);
        if (paths.Count == 0)
            throw new ArgumentException("No test files given, and tests.files in tabulartools.yaml matched none. Create one with 'tabulartools test gen' or generate_measure_tests.");
        var files = paths.Select(MeasureTestFile.Load).ToList();

        var baseDefaulted = string.IsNullOrWhiteSpace(request.Base);
        var baseRef = baseDefaulted ? "HEAD" : request.Base!.Trim();
        var headRef = string.IsNullOrWhiteSpace(request.Head) ? SemanticDiff.WorktreeRef : request.Head!.Trim();
        var strict = request.Strict ?? config?.Tests.Strict ?? false;
        var columnOverrides = config?.Tests.ColumnOverrides ?? false;
        var timeout = config?.Tests.TimeoutSeconds ?? DaxQueryRequest.DefaultTimeoutSeconds;
        var gate = new SemaphoreSlim(config?.Tests.MaxParallel ?? 4);
        var caseFilter = request.CaseIds is { Count: > 0 } ids ? ids.ToHashSet(StringComparer.OrdinalIgnoreCase) : null;

        var executors = new Dictionary<string, IDaxExecutor>(StringComparer.Ordinal);
        var queries = new QueryCache(gate, timeout);
        var sides = new Dictionary<string, Side>(StringComparer.Ordinal);
        var fileResults = new List<TestFileResult>();
        DiffRun? anyRun = null;

        foreach (var file in files)
        {
            var profile = ConnectionProfiles.Resolve(config, request.Profile, request.Environment, file.Profile);
            if (!request.AllowLive && !profile.IsRecorded)
                throw new ArgumentException($"Profile '{profile.Name}' connects to a live model, and live queries are disabled on this server (--disable-live). Use a profile with recordings.");
            if (!executors.TryGetValue(profile.Name, out var executor))
            {
                executor = profile.IsRecorded ? new RecordedDaxExecutor(profile.RecordingsPath!, profile.Database) : factory.Create(profile);
                if (request.WrapExecutor is not null) executor = request.WrapExecutor(executor);
                executors[profile.Name] = executor;
            }

            if (!sides.TryGetValue(file.ModelPath, out var side))
            {
                side = LoadSides(file, baseRef, baseDefaulted, headRef, request.MergeBase, config, columnOverrides);
                sides[file.ModelPath] = side;
            }
            anyRun ??= side.Run;

            var results = new List<Task<AssertionResult>>();
            foreach (var testCase in file.Cases)
            {
                if (caseFilter is not null && !caseFilter.Contains(testCase.Id!)) continue;
                var resolved = CaseResolver.Resolve(testCase, file, side.Head.Snapshot, side.Head.Dependencies, config?.Tests.Tolerance);
                foreach (var (kind, assertion) in resolved.Assertions)
                {
                    if (request.Mode is { } mode && kind != mode && kind != AssertionKind.Invariant) continue;
                    results.Add(RunAssertion(resolved, kind, assertion, side.Plan, executor, queries, cancellationToken));
                }
            }
            var done = await Task.WhenAll(results).ConfigureAwait(false);
            fileResults.Add(new TestFileResult
            {
                File = Relative(cwd, file.Path!),
                Model = Relative(cwd, file.ModelPath),
                Summary = TestSummary.Of(done),
                Overrides = Summarize(side.Plan),
                StructuralChangesNotTested = side.Plan.StructuralChangesNotTested,
                Results = done,
                Diagnostics = side.Diagnostics.Count > 0 ? side.Diagnostics : null,
            });
        }

        var all = fileResults.SelectMany(f => f.Results).ToList();
        var summary = TestSummary.Of(all);
        return new MeasureTestRunResult
        {
            Summary = summary,
            Base = anyRun?.Result.Base,
            Head = anyRun?.Result.Head,
            MergeBase = anyRun?.Result.MergeBase ?? false,
            Profiles = executors.Keys.ToList(),
            Targets = executors.Values.Select(e => e.Target).Distinct().ToList(),
            Strict = strict,
            Blocking = summary.Failed > 0 || summary.Errored > 0 || strict && summary.Changed > 0,
            Files = fileResults,
            QueriesRun = queries.Executed,
        };
    }

    /// <summary>Test files matched by <c>tests.files</c> globs, relative to the config file's folder.</summary>
    public static List<string> FindTestFiles(TabularToolsConfig? config)
    {
        if (config?.Tests.Files is not { Count: > 0 } globs || config.Directory is null) return [];
        var patterns = globs.Select(g => LintEngine.Glob(g.Replace('\\', '/').TrimStart('.', '/'), segmentAware: true)).ToList();
        return Directory.EnumerateFiles(config.Directory, "*.yaml", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => patterns.Any(p => p.IsMatch(Path.GetRelativePath(config.Directory, f).Replace('\\', '/'))))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private sealed record Side(LoadedModel Head, OverridePlan Plan, DiffRun? Run, List<Diagnostic> Diagnostics);

    private static Side LoadSides(MeasureTestFile file, string baseRef, bool baseDefaulted, string headRef, bool mergeBase, TabularToolsConfig? config, bool columnOverrides)
    {
        var diagnostics = new List<Diagnostic>();
        DiffRun run;
        try
        {
            run = SemanticDiff.Run(new DiffRequest
            {
                Base = baseRef,
                Head = headRef,
                Repo = file.Directory,
                ModelPath = file.ModelPath,
                MergeBase = mergeBase,
                IncludeFormattingOnly = false,
                ConfigPath = config?.Path,
                WorkingDirectory = file.Directory,
            });
        }
        catch (GitException ex) when (baseDefaulted && headRef.Equals(SemanticDiff.WorktreeRef, StringComparison.OrdinalIgnoreCase))
        {
            // Not in a git repository (or no commits yet): run what doesn't need a base.
            var model = ModelLoader.Load(file.ModelPath);
            diagnostics.Add(new Diagnostic(DiagnosticLevel.Info, "TEST001", $"No base version to compare with ({ex.Message.Split('.')[0]}); differential assertions are skipped."));
            return new Side(model, OverridePlan.HeadOnly(model.Snapshot), null, diagnostics);
        }

        var comparison = run.Models.FirstOrDefault()
            ?? throw new ArgumentException($"No model at {file.ModelPath} in {baseRef} or {headRef}.");
        if (comparison.Head is null)
            throw new ArgumentException($"The model {file.ModelPath} doesn't exist in {run.Result.Head.Label}, so its tests can't run.");
        if (comparison.Base is null)
        {
            diagnostics.Add(new Diagnostic(DiagnosticLevel.Info, "TEST002", $"The model is new since {run.Result.Base.Label}; differential assertions have nothing to compare with."));
            return new Side(comparison.Head.Model, OverridePlan.HeadOnly(comparison.Head.Model.Snapshot), run, diagnostics);
        }
        var plan = OverridePlan.Build(comparison.Base.Model.Snapshot, comparison.Head.Model.Snapshot, comparison.Outcome.Match, comparison.Diff.Changes, columnOverrides,
            comparison.Base.Model.Dependencies, comparison.Head.Model.Dependencies);
        return new Side(comparison.Head.Model, plan, run, diagnostics);
    }

    private static async Task<AssertionResult> RunAssertion(ResolvedCase c, AssertionKind kind, TestAssertion assertion, OverridePlan plan,
        IDaxExecutor executor, QueryCache queries, CancellationToken ct)
    {
        var result = new AssertionResult
        {
            CaseId = c.Id,
            Assertion = AssertionKinds.Name(kind),
            Status = TestStatus.Error,
            Description = assertion.Description ?? c.Case.Description,
            Role = c.Case.AsRole,
        };
        if (c.Problems.Count > 0) return result with { Message = "Can't run against the model: " + string.Join(" ", c.Problems) };

        try
        {
            return kind switch
            {
                AssertionKind.Differential => await Differential(c, plan, executor, queries, result, ct).ConfigureAwait(false),
                AssertionKind.Invariant => await Invariant(c, assertion, plan, executor, queries, result, ct).ConfigureAwait(false),
                _ => await Snapshot(c, assertion, plan, executor, queries, result, ct).ConfigureAwait(false),
            };
        }
        catch (DaxQueryException ex)
        {
            return result with { Message = ex.Message };
        }
    }

    private static async Task<AssertionResult> Differential(ResolvedCase c, OverridePlan plan, IDaxExecutor executor, QueryCache queries, AssertionResult result, CancellationToken ct)
    {
        if (plan.NoBase) return result with { Status = TestStatus.Skipped, Message = "No base version to compare with." };

        var head = QueryPlanner.Values(c, plan.HeadFor(HeadRoots(c)), m => m.Name, $"{c.Id}/head");
        var @base = QueryPlanner.Values(c, plan.BaseFor(BaseRoots(c, plan)), m => plan.Added.Contains(m.Key) ? null : plan.BaseNames.GetValueOrDefault(m.Key, m.Name), $"{c.Id}/base");
        var same = @base.Text == head.Text;
        if (same) head = head with { Label = c.Id };

        var headRun = queries.Run(executor, head, ct);
        var baseRun = same ? headRun : queries.Run(executor, @base, ct);
        var (headResult, headError) = await Try(headRun).ConfigureAwait(false);
        var (baseResult, baseError) = await Try(baseRun).ConfigureAwait(false);

        if (headError is not null && baseError is not null)
            return result with { Message = same || headError == baseError ? headError : $"Both queries failed. Base: {baseError} Head: {headError}" };
        if (headError is not null)
            return result with { Status = TestStatus.Fail, Message = $"The head query fails, but the base query runs: {headError}" };
        if (baseError is not null)
            return result with { Message = $"The base query fails, so there's nothing to compare with: {baseError}" };

        var (measures, diffs) = ResultComparer.Differential(c, ResultComparer.Index(@base, baseResult!), ResultComparer.Index(head, headResult!), plan.Added, plan.DirectlyEdited);
        var regression = measures.Any(m => m.Change == MeasureChange.Regression);
        var expected = measures.Any(m => m.Change == MeasureChange.ExpectedChange);
        var status = regression ? TestStatus.Fail : expected ? TestStatus.Changed : TestStatus.Pass;
        var added = measures.Where(m => m.Change == MeasureChange.Added).Select(m => m.Measure).ToList();
        string? message = regression
            ? $"Changed without being edited: {string.Join(", ", measures.Where(m => m.Change == MeasureChange.Regression).Select(m => $"[{m.Measure}] ({Rows(m.ChangedRows)})"))}."
            : expected ? $"Expected change in edited measure(s): {string.Join(", ", measures.Where(m => m.Change == MeasureChange.ExpectedChange).Select(m => $"[{m.Measure}] ({Rows(m.ChangedRows)})"))}." : null;
        if (added.Count > 0) message = (message is null ? "" : message + " ") + $"New measure(s), not compared: {string.Join(", ", added.Select(a => $"[{a}]"))}.";
        return result with
        {
            Status = status,
            ExpectedChange = expected && !regression,
            Measures = measures,
            Diffs = diffs.Take(MaxDiffsListed).ToList(),
            DiffCount = diffs.Count,
            Message = message,
        };
    }

    private static async Task<AssertionResult> Invariant(ResolvedCase c, TestAssertion assertion, OverridePlan plan, IDaxExecutor executor, QueryCache queries, AssertionResult result, CancellationToken ct)
    {
        var index = c.Assertions.Select(a => a.Assertion).ToList().IndexOf(assertion);
        var query = QueryPlanner.Invariant(c, assertion.Expr!, plan.HeadFor(HeadRoots(c, assertion.Expr)), $"{c.Id}/invariant-{index}");
        var rows = await queries.Run(executor, query, ct).ConfigureAwait(false);
        var indexed = ResultComparer.Index(query, rows);
        var violating = indexed.Order.Select(k => indexed.Rows[k].Key).ToList();
        return result with
        {
            Status = violating.Count == 0 ? TestStatus.Pass : TestStatus.Fail,
            Violations = violating.Count,
            ViolatingRows = violating.Take(MaxViolatingRowsListed).ToList(),
            Message = violating.Count == 0 ? null
                : $"{assertion.Expr} is false on {Rows(violating.Count)}, e.g. {ResultComparer.Describe(violating[0])}.",
        };
    }

    private static async Task<AssertionResult> Snapshot(ResolvedCase c, TestAssertion assertion, OverridePlan plan, IDaxExecutor executor, QueryCache queries, AssertionResult result, CancellationToken ct)
    {
        var defs = plan.HeadFor(HeadRoots(c));
        var query = QueryPlanner.Values(c, defs, m => m.Name, defs.IsEmpty ? c.Id : $"{c.Id}/head");
        var live = ResultComparer.Index(query, await queries.Run(executor, query, ct).ConfigureAwait(false));
        var diffs = ResultComparer.Snapshot(c, assertion, live);
        return result with
        {
            Status = diffs.Count == 0 ? TestStatus.Pass : TestStatus.Fail,
            Diffs = diffs.Take(MaxDiffsListed).ToList(),
            DiffCount = diffs.Count,
            Message = diffs.Count == 0 ? null
                : $"{diffs.Count} value{(diffs.Count == 1 ? "" : "s")} differ from the snapshot captured {assertion.CapturedAt ?? "earlier"}.",
        };
    }

    /// <summary>What a head query evaluates: the case's measures and the objects its filters (and invariant) name.</summary>
    private static IEnumerable<string> HeadRoots(ResolvedCase c, string? extra = null) =>
        c.Measures.Select(m => m.Key).Concat(c.Filters.Append(extra ?? "").SelectMany(f => c.HeadReferences(f)));

    private static IEnumerable<string> BaseRoots(ResolvedCase c, OverridePlan plan) =>
        c.Measures.Where(m => plan.BaseKeys.ContainsKey(m.Key)).Select(m => plan.BaseKeys[m.Key])
            .Concat(c.Filters.SelectMany(f => c.HeadReferences(f)).Select(k => plan.BaseKeys.GetValueOrDefault(k, k)));

    private static string Rows(int n) => n == 1 ? "1 row" : $"{n} rows";

    private static async Task<(DaxResult? Result, string? Error)> Try(Task<DaxResult> task)
    {
        try { return (await task.ConfigureAwait(false), null); }
        catch (DaxQueryException ex) { return (null, ex.Message); }
    }

    private static OverrideSummary? Summarize(OverridePlan plan)
    {
        if (plan.NoBase) return null;
        static IEnumerable<string> Names(SideDefinitions d) =>
            d.Columns.Select(x => QueryPlanner.Column(x.Table, x.Name)).Concat(d.Measures.Select(x => QueryPlanner.Column(x.Table, x.Name)));
        return new OverrideSummary(Names(plan.Base).ToList(), Names(plan.Head).ToList(), plan.DirectlyEdited.Select(k =>
        {
            var (table, name) = DependencyQuery.SplitKey(k);
            return QueryPlanner.Column(table ?? "", name);
        }).Order(StringComparer.Ordinal).ToList());
    }

    private static string Relative(string cwd, string path) => Path.GetRelativePath(cwd, path).Replace('\\', '/');

    /// <summary>Runs each distinct query once (same text and role), at most N at a time.</summary>
    private sealed class QueryCache(SemaphoreSlim gate, int timeoutSeconds)
    {
        private readonly ConcurrentDictionary<string, Lazy<Task<DaxResult>>> _runs = new(StringComparer.Ordinal);
        private int _executed;

        public int Executed => _executed;

        public Task<DaxResult> Run(IDaxExecutor executor, PlannedQuery query, CancellationToken ct)
        {
            var key = executor.Target.Endpoint + "\n" + executor.Target.Database + "\n" + RecordingsFile.Key(query.Text, query.Role);
            return _runs.GetOrAdd(key, _ => new Lazy<Task<DaxResult>>(async () =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    Interlocked.Increment(ref _executed);
                    var result = await executor.ExecuteAsync(new DaxQueryRequest(query.Text, query.Role, MaxRows, timeoutSeconds, query.Label), ct).ConfigureAwait(false);
                    if (result.Truncated) throw new DaxQueryException($"The query returned more than {MaxRows} rows; narrow the case with filters or fewer group-by columns.");
                    return result;
                }
                finally
                {
                    gate.Release();
                }
            })).Value;
        }
    }
}
