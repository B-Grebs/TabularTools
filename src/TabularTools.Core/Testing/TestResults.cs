using System.Text.Json.Serialization;
using TabularTools.Core.Diagnostics;
using TabularTools.Core.Diff;
using TabularTools.Core.Queries;

namespace TabularTools.Core.Testing;

[JsonConverter(typeof(JsonStringEnumConverter<TestStatus>))]
public enum TestStatus
{
    [JsonStringEnumMemberName("pass")] Pass,
    /// <summary>A regression: a value changed that the change didn't edit directly, an invariant failed, or a snapshot didn't match.</summary>
    [JsonStringEnumMemberName("fail")] Fail,
    /// <summary>Only measures the change edited directly changed value (expected; blocking with --strict).</summary>
    [JsonStringEnumMemberName("changed")] Changed,
    /// <summary>The case couldn't be evaluated (unknown measure, query error, no recording).</summary>
    [JsonStringEnumMemberName("error")] Error,
    [JsonStringEnumMemberName("skipped")] Skipped,
}

[JsonConverter(typeof(JsonStringEnumConverter<MeasureChange>))]
public enum MeasureChange
{
    [JsonStringEnumMemberName("unchanged")] Unchanged,
    /// <summary>Changed, and the change edited this measure's DAX.</summary>
    [JsonStringEnumMemberName("expectedChange")] ExpectedChange,
    /// <summary>Changed without being edited: a dependent or a guard measure.</summary>
    [JsonStringEnumMemberName("regression")] Regression,
    /// <summary>New in head; nothing to compare with.</summary>
    [JsonStringEnumMemberName("added")] Added,
}

/// <param name="Key">Group-by values of the row (<c>Table[Column]</c> → value). Empty without group-by columns.</param>
/// <param name="Base">Base value (for snapshots: the stored value). Null is BLANK or a missing row.</param>
/// <param name="Head">Head value (for snapshots: the live value).</param>
/// <param name="Delta">head − base for numbers.</param>
/// <param name="Expected">True when the measure was edited directly, so the change is expected.</param>
public sealed record CellDiff(IReadOnlyDictionary<string, object?> Key, string Measure, object? Base, object? Head, double? Delta, bool Expected);

public sealed record MeasureOutcome(string Measure, MeasureChange Change, int ChangedRows);

public sealed record AssertionResult
{
    public required string CaseId { get; init; }
    /// <summary>differential, invariant or snapshot.</summary>
    public required string Assertion { get; init; }
    public required TestStatus Status { get; init; }
    /// <summary>True when every difference is on a measure the change edited directly.</summary>
    public bool ExpectedChange { get; init; }
    public string? Description { get; init; }
    public string? Role { get; init; }
    public IReadOnlyList<MeasureOutcome>? Measures { get; init; }
    /// <summary>Differences, regressions first (at most <see cref="MeasureTestRunner.MaxDiffsListed"/>).</summary>
    public IReadOnlyList<CellDiff>? Diffs { get; init; }
    /// <summary>All differences, including those not listed.</summary>
    public int DiffCount { get; init; }
    /// <summary>Invariant: rows where the condition is false.</summary>
    public int? Violations { get; init; }
    /// <summary>Invariant: group-by values of the first violating rows.</summary>
    public IReadOnlyList<IReadOnlyDictionary<string, object?>>? ViolatingRows { get; init; }
    public string? Message { get; init; }
}

public sealed record TestSummary(int Passed, int Failed, int Changed, int Errored, int Skipped)
{
    public int Total => Passed + Failed + Changed + Errored + Skipped;

    public static TestSummary Of(IEnumerable<AssertionResult> results)
    {
        var list = results.ToList();
        return new(list.Count(r => r.Status == TestStatus.Pass), list.Count(r => r.Status == TestStatus.Fail), list.Count(r => r.Status == TestStatus.Changed),
            list.Count(r => r.Status == TestStatus.Error), list.Count(r => r.Status == TestStatus.Skipped));
    }
}

/// <summary>What a run overrode on each side (measure and column names as <c>'Table'[Name]</c>).</summary>
public sealed record OverrideSummary(IReadOnlyList<string> Base, IReadOnlyList<string> Head, IReadOnlyList<string> DirectlyEdited);

public sealed record TestFileResult
{
    /// <summary>Test file path, relative to the working folder.</summary>
    public required string File { get; init; }
    public required string Model { get; init; }
    public required TestSummary Summary { get; init; }
    public OverrideSummary? Overrides { get; init; }
    /// <summary>Changes between base and head that tests can't simulate, so they're not covered by this run.</summary>
    public IReadOnlyList<Change> StructuralChangesNotTested { get; init; } = [];
    public required IReadOnlyList<AssertionResult> Results { get; init; }
    public IReadOnlyList<Diagnostic>? Diagnostics { get; init; }
}

public sealed record MeasureTestRunResult
{
    public int SchemaVersion { get; init; } = 1;
    public required TestSummary Summary { get; init; }
    public DiffSide? Base { get; init; }
    public DiffSide? Head { get; init; }
    public bool MergeBase { get; init; }
    /// <summary>Profile names used.</summary>
    public required IReadOnlyList<string> Profiles { get; init; }
    public required IReadOnlyList<DaxTarget> Targets { get; init; }
    public bool Strict { get; init; }
    /// <summary>True when something failed or errored (or changed, with strict).</summary>
    public required bool Blocking { get; init; }
    public required IReadOnlyList<TestFileResult> Files { get; init; }
    public int QueriesRun { get; init; }
    public IReadOnlyList<Diagnostic>? Diagnostics { get; init; }
}
