using System.Text.Json;
using System.Text.RegularExpressions;
using TabularTools.Core.Config;
using TabularTools.Core.Describe;
using TabularTools.Core.Lint.Compat;
using TabularTools.Core.Lint.Rules;
using TabularTools.Core.Loading;
using TabularTools.Core.Snapshots;

namespace TabularTools.Core.Lint;

public sealed record LintOptions
{
    /// <summary>Ruleset specs; overrides the config. Default: config, else builtin + microsoft-bpa.</summary>
    public IReadOnlyList<string>? Rulesets { get; init; }

    /// <summary>Only report violations at or above this severity.</summary>
    public Severity? MinSeverity { get; init; }

    /// <summary>Only run these rules.</summary>
    public IReadOnlyList<string>? RuleIds { get; init; }

    public FailOn? FailOn { get; init; }

    public TabularToolsConfig? Config { get; init; }

    /// <summary>Folder relative ruleset paths resolve against when they don't come from a config file.</summary>
    public string? BaseDirectory { get; init; }
}

/// <summary>Runs rulesets against a model and applies severity overrides and suppressions.</summary>
public static class LintEngine
{
    public const string IgnoreAnnotation = "BestPracticeAnalyzer_IgnoreRules";
    public const string ModelRulesAnnotation = "BestPracticeAnalyzer";

    public static IReadOnlyList<Ruleset> LoadRulesets(LintOptions options, LoadedModel? model = null)
    {
        var lint = options.Config?.Lint;
        var fromCli = options.Rulesets is { Count: > 0 };
        var specs = fromCli ? options.Rulesets! : lint?.Rulesets ?? RulesetLoader.Defaults;
        var baseDir = fromCli ? options.BaseDirectory ?? Directory.GetCurrentDirectory()
            : options.Config?.Directory ?? options.BaseDirectory ?? Directory.GetCurrentDirectory();

        var rulesets = specs.Select(s => RulesetLoader.Load(s, baseDir)).ToList();
        if (model is not null && lint?.UseModelRules == true &&
            model.Database.Model.Annotations.Find(ModelRulesAnnotation)?.Value is { Length: > 0 } embedded)
            rulesets.Add(RulesetLoader.FromJson("model", "model-annotation", embedded, null));
        return rulesets;
    }

    public static LintResult Run(LoadedModel model, LintOptions options, string? handle = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(options);
        var lint = options.Config?.Lint ?? new LintSettings();
        var rulesets = LoadRulesets(options, model);
        var context = new LintContext(model);

        var severityOverrides = (lint.Severity ?? []).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        var disabled = new HashSet<string>(lint.Disabled ?? [], StringComparer.OrdinalIgnoreCase);
        var only = options.RuleIds is { Count: > 0 } ? new HashSet<string>(options.RuleIds, StringComparer.OrdinalIgnoreCase) : null;
        var suppressions = (lint.Suppress ?? []).Select(s => (Rule: Glob(s.Rule!, segmentAware: false), Objects: Glob(s.Objects ?? "**", segmentAware: true))).ToList();
        var ignores = new IgnoreIndex(context);

        var violations = new List<Violation>();
        var rulesUsed = new Dictionary<string, RuleInfo>(StringComparer.Ordinal);
        var skipped = new List<SkippedRule>();
        var errors = new List<RuleError>();
        var seenIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int evaluated = 0, suppressed = 0;

        foreach (var ruleset in rulesets)
        {
            foreach (var rule in ruleset.Rules)
            {
                var info = rule.Info;
                if (only is not null && !only.Contains(info.Id)) continue;
                if (seenIds.TryGetValue(info.Id, out var firstRuleset))
                {
                    skipped.Add(new SkippedRule(info.Id, ruleset.Id, $"Duplicate rule id; already defined in '{firstRuleset}'."));
                    continue;
                }
                seenIds[info.Id] = ruleset.Id;

                var severity = info.Severity;
                if (severityOverrides.TryGetValue(info.Id, out var overrideText))
                {
                    if (overrideText.Equals("off", StringComparison.OrdinalIgnoreCase)) continue;
                    SeverityParsing.TryParse(overrideText, out severity);
                }
                if (disabled.Contains(info.Id)) continue;

                var result = rule.Evaluate(context);
                if (result.SkipReason is not null)
                {
                    skipped.Add(new SkippedRule(info.Id, ruleset.Id, result.SkipReason));
                    continue;
                }
                evaluated++;
                if (result.ErrorCount > 0) errors.Add(new RuleError(info.Id, result.ErrorCount, result.FirstError ?? "unknown error"));

                foreach (var hit in result.Hits)
                {
                    if (ignores.IsIgnored(info.Id, hit.Object.Key) ||
                        suppressions.Any(s => s.Rule.IsMatch(info.Id) && s.Objects.IsMatch(hit.Object.Key)))
                    {
                        suppressed++;
                        continue;
                    }
                    if (options.MinSeverity is { } min && severity < min) continue;

                    violations.Add(new Violation
                    {
                        RuleId = info.Id,
                        Severity = severity,
                        Object = hit.Object,
                        Message = hit.Detail is null ? info.Name : $"{info.Name}: {hit.Detail}",
                        FixExpression = info.FixExpression,
                    });
                    rulesUsed.TryAdd(info.Id, info with { Severity = severity, Expression = null });
                }
            }
        }

        violations = violations
            .OrderByDescending(v => v.Severity)
            .ThenBy(v => v.RuleId, StringComparer.Ordinal)
            .ThenBy(v => v.Object.Key, StringComparer.Ordinal)
            .ToList();

        var failOn = options.FailOn ?? (lint.FailOn is { } f ? SeverityParsing.ParseFailOn(f) : FailOn.Error);
        var blocking = failOn != FailOn.None && violations.Any(v => (int)v.Severity >= (int)failOn);

        return new LintResult
        {
            Model = ModelDescriber.Info(model, handle),
            Rulesets = rulesets.Select(r => new RulesetSummary(r.Id, r.Source, r.Rules.Count, r.Path)).ToList(),
            Violations = violations,
            Rules = rulesUsed,
            SkippedRules = skipped,
            RuleErrors = errors,
            Summary = new LintSummary(
                violations.Count(v => v.Severity == Severity.Error),
                violations.Count(v => v.Severity == Severity.Warning),
                violations.Count(v => v.Severity == Severity.Info),
                violations.Count, suppressed, evaluated, skipped.Count),
            FailOn = failOn,
            Blocking = blocking,
            ConfigPath = options.Config?.Path,
        };
    }

    /// <summary>
    /// Glob to regex. With <paramref name="segmentAware"/>, <c>*</c> stays inside a '/'-separated segment and <c>**</c> crosses them;
    /// otherwise <c>*</c> matches anything. Case-insensitive.
    /// </summary>
    internal static Regex Glob(string glob, bool segmentAware)
    {
        var pattern = Regex.Escape(glob).Replace("\\*\\*", "\u0001", StringComparison.Ordinal).Replace("\\*", segmentAware ? "[^/]*" : ".*", StringComparison.Ordinal)
            .Replace("\u0001", ".*", StringComparison.Ordinal).Replace("\\?", ".", StringComparison.Ordinal);
        return new Regex("^" + pattern + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// Tabular Editor's per-object suppression: annotation <c>BestPracticeAnalyzer_IgnoreRules = {"RuleIDs":[...]}</c>.
    /// Applies to the object itself, objects in an annotated table, and (on the model) to everything.
    /// </summary>
    private sealed class IgnoreIndex(LintContext context)
    {
        private readonly Dictionary<string, HashSet<string>> _cache = new(StringComparer.Ordinal);

        public bool IsIgnored(string ruleId, string key)
        {
            if (For(key).Contains(ruleId) || For(ObjectKeys.Model).Contains(ruleId)) return true;
            var (table, _) = Dax.DependencyQuery.SplitKey(key);
            return table is not null && For(ObjectKeys.Table(table)).Contains(ruleId);
        }

        private HashSet<string> For(string key)
        {
            if (_cache.TryGetValue(key, out var set)) return set;
            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (context.View.Find(key) is { } obj && obj.GetAnnotation(IgnoreAnnotation) is { Length: > 0 } json)
            {
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("RuleIDs", out var ids) && ids.ValueKind == JsonValueKind.Array)
                        foreach (var id in ids.EnumerateArray())
                            if (id.GetString() is { } s) set.Add(s);
                }
                catch (JsonException)
                {
                    // A malformed annotation suppresses nothing.
                }
            }
            return _cache[key] = set;
        }
    }
}
