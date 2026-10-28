using System.Text.Json.Serialization;
using TabularTools.Core.Dax;
using TabularTools.Core.Diagnostics;

namespace TabularTools.Core.Diff;

/// <summary>How much a change matters to consumers of the model (ARCHITECTURE §10.5).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ChangeSeverity>))]
public enum ChangeSeverity
{
    /// <summary>No semantic effect: formatting-only expression changes, added objects.</summary>
    [JsonStringEnumMemberName("info")] Info = 1,
    /// <summary>Cosmetic but visible: format strings, folders, descriptions.</summary>
    [JsonStringEnumMemberName("low")] Low = 2,
    /// <summary>Changes defaults or behaviour in reports: summarize-by, sort-by, hiding.</summary>
    [JsonStringEnumMemberName("medium")] Medium = 3,
    /// <summary>Changes results: DAX, relationships, data types, sources.</summary>
    [JsonStringEnumMemberName("high")] High = 4,
    /// <summary>Likely to break consumers or change security: removals, renames, RLS.</summary>
    [JsonStringEnumMemberName("breaking")] Breaking = 5,
}

/// <summary>Lowest change severity that makes <c>diff</c> exit with "blocking findings".</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DiffFailOn>))]
public enum DiffFailOn
{
    [JsonStringEnumMemberName("none")] None = 0,
    [JsonStringEnumMemberName("info")] Info = 1,
    [JsonStringEnumMemberName("low")] Low = 2,
    [JsonStringEnumMemberName("medium")] Medium = 3,
    [JsonStringEnumMemberName("high")] High = 4,
    [JsonStringEnumMemberName("breaking")] Breaking = 5,
}

public static class ChangeSeverityParsing
{
    public static bool TryParse(string? text, out ChangeSeverity severity)
    {
        severity = ChangeSeverity.Info;
        switch (text?.Trim().ToLowerInvariant())
        {
            case "info": severity = ChangeSeverity.Info; return true;
            case "low": severity = ChangeSeverity.Low; return true;
            case "medium": severity = ChangeSeverity.Medium; return true;
            case "high": severity = ChangeSeverity.High; return true;
            case "breaking": severity = ChangeSeverity.Breaking; return true;
            default: return false;
        }
    }

    public static DiffFailOn ParseFailOn(string text)
    {
        if (text.Trim().Equals("none", StringComparison.OrdinalIgnoreCase)) return DiffFailOn.None;
        if (TryParse(text, out var s)) return (DiffFailOn)(int)s;
        throw new ArgumentException($"Invalid diff failOn value '{text}'. Use breaking, high, medium, low, info or none.");
    }
}

/// <summary>How a renamed object was recognised.</summary>
/// <param name="From">Name in the base version.</param>
/// <param name="To">Name in the head version.</param>
/// <param name="Confidence"><c>certain</c> (lineage tag), <c>high</c> (score ≥ 0.9) or <c>medium</c>.</param>
/// <param name="Evidence"><c>lineageTag</c>, <c>expression</c>, <c>sourceColumn</c>, <c>columns</c> or <c>levels</c>.</param>
/// <param name="Score">Similarity score for heuristic matches.</param>
public sealed record RenameInfo(string From, string To, string Confidence, string Evidence, double? Score = null);

public sealed record ChangeDetails
{
    /// <summary>True when an expression changed only in whitespace or comments.</summary>
    public bool? FormattingOnly { get; init; }
    /// <summary>Unified line diff of a changed expression.</summary>
    public string? ExpressionDiff { get; init; }
    /// <summary>Free-form explanation, e.g. "relationship key column" or "3 columns, 2 measures".</summary>
    public string? Note { get; init; }
}

/// <summary>What else a change reaches.</summary>
/// <param name="Downstream">Keys of objects that use the changed object, directly or indirectly (first 25).</param>
/// <param name="DownstreamCount">How many downstream objects there are in total.</param>
/// <param name="Reports">Reports that use the model (filled by lineage, M5).</param>
public sealed record ChangeImpact(IReadOnlyList<string> Downstream, int DownstreamCount, IReadOnlyList<string>? Reports = null);

/// <summary>Where an object is defined.</summary>
/// <param name="File">Repo-relative path (forward slashes), or a path relative to the working folder for folder diffs.</param>
public sealed record SourceLocation(string File, int Line);

/// <summary>One semantic change (ARCHITECTURE §10.6).</summary>
public sealed record Change
{
    /// <summary>Stable id: <c>kind:objectKey</c>.</summary>
    public required string Id { get; init; }
    /// <summary>Change kind, e.g. <c>measure.expressionChanged</c>. The full list is in docs/diff-severities.md.</summary>
    public required string Kind { get; init; }
    public required ChangeSeverity Severity { get; init; }
    /// <summary>The object as it is in head (or in base, for removed objects).</summary>
    public required ObjectRef Object { get; init; }
    public RenameInfo? Rename { get; init; }
    /// <summary>Old value (or expression).</summary>
    public string? Before { get; init; }
    /// <summary>New value (or expression).</summary>
    public string? After { get; init; }
    public ChangeDetails? Details { get; init; }
    public ChangeImpact? Impact { get; init; }
    public SourceLocation? Location { get; init; }
}

public sealed record DiffSummary(int Breaking, int High, int Medium, int Low, int Info, int Total, int FormattingOnlyHidden);

[JsonConverter(typeof(JsonStringEnumConverter<ModelDiffStatus>))]
public enum ModelDiffStatus
{
    [JsonStringEnumMemberName("modified")] Modified,
    [JsonStringEnumMemberName("unchanged")] Unchanged,
    [JsonStringEnumMemberName("added")] Added,
    [JsonStringEnumMemberName("removed")] Removed,
}

public sealed record ModelDiff
{
    /// <summary>Repo-relative path of the model (usually a <c>*.SemanticModel</c> folder), or the folder given for folder diffs.</summary>
    public required string ModelPath { get; init; }
    public required string Name { get; init; }
    public required ModelDiffStatus Status { get; init; }
    public required DiffSummary Summary { get; init; }
    /// <summary>Most severe first.</summary>
    public required IReadOnlyList<Change> Changes { get; init; }
    /// <summary>PR-comment Markdown for this model, when requested.</summary>
    public string? Markdown { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<DiffSideKind>))]
public enum DiffSideKind
{
    /// <summary>A git commit.</summary>
    [JsonStringEnumMemberName("ref")] Ref,
    /// <summary>The files on disk in the repository's working tree.</summary>
    [JsonStringEnumMemberName("worktree")] Worktree,
    /// <summary>A folder or file outside git.</summary>
    [JsonStringEnumMemberName("folder")] Folder,
}

/// <param name="Ref">The ref as given (e.g. <c>origin/main</c>).</param>
/// <param name="Commit">Full commit id (after merge-base, for the base side).</param>
/// <param name="Path">Folder path for folder sides.</param>
public sealed record DiffSide(DiffSideKind Kind, string? Ref = null, string? Commit = null, string? Path = null)
{
    public string Label => Kind switch
    {
        DiffSideKind.Ref => Commit is null ? Ref ?? "?" : Git.GitCliReader.Short(Commit),
        DiffSideKind.Worktree => "working tree",
        _ => Path ?? "?",
    };
}

public sealed record DiffResult
{
    public int SchemaVersion { get; init; } = 1;
    public required DiffSide Base { get; init; }
    public required DiffSide Head { get; init; }
    /// <summary>True when the base side is the merge base of the given base and head (what a PR shows).</summary>
    public bool MergeBase { get; init; }
    public required IReadOnlyList<ModelDiff> Models { get; init; }
    public required DiffFailOn FailOn { get; init; }
    /// <summary>True when any change is at or above <see cref="FailOn"/>.</summary>
    public required bool Blocking { get; init; }
    public IReadOnlyList<Diagnostic>? Diagnostics { get; init; }
    public string? ConfigPath { get; init; }
}
