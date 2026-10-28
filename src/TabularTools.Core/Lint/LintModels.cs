using System.Text.Json.Serialization;
using TabularTools.Core.Dax;

namespace TabularTools.Core.Lint;

[JsonConverter(typeof(JsonStringEnumConverter<Severity>))]
public enum Severity
{
    [JsonStringEnumMemberName("info")] Info = 1,
    [JsonStringEnumMemberName("warning")] Warning = 2,
    [JsonStringEnumMemberName("error")] Error = 3,
}

/// <summary>Lowest severity that makes the CLI exit with "blocking findings".</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FailOn>))]
public enum FailOn
{
    [JsonStringEnumMemberName("none")] None = 0,
    [JsonStringEnumMemberName("info")] Info = 1,
    [JsonStringEnumMemberName("warning")] Warning = 2,
    [JsonStringEnumMemberName("error")] Error = 3,
}

public static class SeverityParsing
{
    /// <summary>BPA severity 1 → info, 2 → warning, 3+ → error.</summary>
    public static Severity FromBpa(int value) => value <= 1 ? Severity.Info : value == 2 ? Severity.Warning : Severity.Error;

    public static bool TryParse(string? text, out Severity severity)
    {
        severity = Severity.Warning;
        switch (text?.Trim().ToLowerInvariant())
        {
            case "info": case "information": case "1": severity = Severity.Info; return true;
            case "warning": case "warn": case "2": severity = Severity.Warning; return true;
            case "error": case "3": case "4": case "5": severity = Severity.Error; return true;
            default: return false;
        }
    }

    public static FailOn ParseFailOn(string text) => text.Trim().ToLowerInvariant() switch
    {
        "none" => FailOn.None,
        "info" => FailOn.Info,
        "warning" or "warn" => FailOn.Warning,
        "error" => FailOn.Error,
        _ => throw new ArgumentException($"Invalid failOn value '{text}'. Use error, warning, info or none."),
    };
}

/// <summary>What a rule is, independent of how it is evaluated.</summary>
public sealed record RuleInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Ruleset { get; init; }
    public string? Category { get; init; }
    public string? Description { get; init; }
    public required Severity Severity { get; init; }
    public required IReadOnlyList<string> Scope { get; init; }
    public string? Expression { get; init; }
    public string? FixExpression { get; init; }
    public string? HelpUrl { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }
}

public sealed record Violation
{
    public required string RuleId { get; init; }
    public required Severity Severity { get; init; }
    public required ObjectRef Object { get; init; }
    public required string Message { get; init; }
    public string? FixExpression { get; init; }
    /// <summary>Set in diff-aware mode: true when the violation is new since the base ref.</summary>
    public bool? IsNew { get; init; }
}

public sealed record SkippedRule(string RuleId, string Ruleset, string Reason);

public sealed record RuleError(string RuleId, int Objects, string Message);

public sealed record RulesetSummary(string Id, string Source, int Rules, string? Path = null);

/// <param name="New">Diff-aware mode: violations introduced since the base ref.</param>
/// <param name="Existing">Diff-aware mode: violations that were already there (not blocking).</param>
public sealed record LintSummary(int Error, int Warning, int Info, int Total, int Suppressed, int RulesEvaluated, int RulesSkipped, int? New = null, int? Existing = null);

public sealed record LintResult
{
    public int SchemaVersion { get; init; } = 1;
    public required Describe.ModelInfo Model { get; init; }
    public required IReadOnlyList<RulesetSummary> Rulesets { get; init; }
    public required IReadOnlyList<Violation> Violations { get; init; }
    /// <summary>Rules that produced at least one violation, keyed by rule id (so descriptions aren't repeated per violation).</summary>
    public required IReadOnlyDictionary<string, RuleInfo> Rules { get; init; }
    public required IReadOnlyList<SkippedRule> SkippedRules { get; init; }
    public required IReadOnlyList<RuleError> RuleErrors { get; init; }
    public required LintSummary Summary { get; init; }
    public required FailOn FailOn { get; init; }
    /// <summary>True when any violation is at or above <see cref="FailOn"/> (in diff-aware mode: any new violation).</summary>
    public required bool Blocking { get; init; }
    /// <summary>Diff-aware mode: the base ref as given.</summary>
    public string? BaseRef { get; init; }
    /// <summary>Diff-aware mode: the commit the model was compared with (merge-base of the base ref and HEAD).</summary>
    public string? BaseCommit { get; init; }
    public string? ConfigPath { get; init; }
    /// <summary>True when <see cref="Violations"/> was cut to a limit; <see cref="Summary"/> still counts everything.</summary>
    public bool Truncated { get; init; }
}

/// <summary>Result of checking a rules file without a model.</summary>
public sealed record RuleCheck(string RuleId, string Name, IReadOnlyList<string> Scope, bool Ok, string? Problem = null);
