using System.Collections.Concurrent;
using System.Linq.Dynamic.Core;
using System.Linq.Dynamic.Core.CustomTypeProviders;
using System.Linq.Dynamic.Core.Exceptions;
using System.Reflection;
using TabularTools.Core.Lint.Compat;

namespace TabularTools.Core.Lint.Rules;

/// <summary>A Best Practice Analyzer rule: a Dynamic LINQ predicate evaluated against each object in its scope.</summary>
public sealed class BpaRule : ILintRule
{
    private static readonly ParsingConfig Config = new()
    {
        CustomTypeProvider = new CompatTypeProvider(),
        AllowNewToEvaluateAnyType = false,
    };

    private static readonly ConcurrentDictionary<(string Expression, Type Type), Result<Delegate>> Compiled = new();

    private readonly BpaRuleDefinition _definition;

    public BpaRule(BpaRuleDefinition definition, string ruleset)
    {
        _definition = definition;
        var severity = SeverityParsing.TryParse(definition.Extension?.Severity, out var s) ? s : SeverityParsing.FromBpa(definition.Severity);
        Info = new RuleInfo
        {
            Id = string.IsNullOrWhiteSpace(definition.ID) ? $"{ruleset}:{definition.Name}" : definition.ID,
            Name = definition.Name ?? definition.ID ?? "(unnamed rule)",
            Ruleset = ruleset,
            Category = definition.Category,
            Description = definition.Description,
            Severity = severity,
            Scope = (definition.Scope ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            Expression = definition.Expression,
            FixExpression = string.IsNullOrWhiteSpace(definition.FixExpression) ? null : definition.FixExpression,
            HelpUrl = definition.Extension?.HelpUrl,
            Tags = definition.Extension?.Tags,
        };
    }

    public RuleInfo Info { get; }

    public RuleEvaluation Evaluate(LintContext context)
    {
        if (string.IsNullOrWhiteSpace(_definition.Expression)) return RuleEvaluation.Skipped("The rule has no expression.");
        if (_definition.CompatibilityLevel is { } cl && cl > context.Model.Snapshot.CompatibilityLevel)
            return RuleEvaluation.Skipped($"Needs compatibility level {cl}; the model is {context.Model.Snapshot.CompatibilityLevel}.");

        var scopes = Info.Scope.Select(s => (Name: s, Objects: Scopes.Objects(context.View, s))).ToList();
        var unknown = scopes.Where(s => s.Objects is null).Select(s => s.Name).ToList();
        if (scopes.Count == 0 || unknown.Count == scopes.Count)
            return RuleEvaluation.Skipped(scopes.Count == 0 ? "The rule has no scope." : $"Unsupported scope: {string.Join(", ", unknown)}.");

        var translated = DialectAdapter.Translate(_definition.Expression);
        var hits = new List<RuleHit>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var errors = 0;
        string? firstError = null;

        foreach (var group in scopes.Where(s => s.Objects is not null).SelectMany(s => s.Objects!).GroupBy(o => o.GetType()))
        {
            var compiled = Compiled.GetOrAdd((translated, group.Key), key => Compile(key.Expression, key.Type));
            if (compiled.Error is not null) return RuleEvaluation.Skipped($"Expression not supported: {compiled.Error}");

            foreach (var obj in group)
            {
                if (!seen.Add(obj.Key)) continue;
                try
                {
                    if (compiled.Value!.DynamicInvoke(obj) is true) hits.Add(new RuleHit(LintContext.RefOf(obj)));
                }
                catch (TargetInvocationException ex)
                {
                    errors++;
                    firstError ??= $"{obj.Key}: {ex.InnerException?.GetType().Name}: {ex.InnerException?.Message}";
                }
            }
        }
        return new RuleEvaluation(hits, null, errors, firstError);
    }

    private static Result<Delegate> Compile(string expression, Type type)
    {
        try
        {
            var lambda = DynamicExpressionParser.ParseLambda(Config, false, type, typeof(bool), expression);
            return new Result<Delegate>(lambda.Compile(), null);
        }
        catch (Exception ex) when (ex is ParseException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return new Result<Delegate>(null, ex.Message);
        }
    }

    private sealed record Result<T>(T? Value, string? Error);

    /// <summary>
    /// Makes the compatibility view, RegEx, StringComparison and Object (for ToString/Equals) visible to rule expressions; nothing else.
    /// System.Type isn't registered, so GetType() leads nowhere.
    /// </summary>
    private sealed class CompatTypeProvider() : DefaultDynamicLinqCustomTypeProvider(ParsingConfig.Default,
        [.. typeof(TeObject).Assembly.GetTypes().Where(t => t.IsPublic && t.Namespace == typeof(TeObject).Namespace), typeof(StringComparison), typeof(object)], true);
}

/// <summary>Maps BPA scope names to the objects they cover.</summary>
internal static class Scopes
{
    public static IEnumerable<TeObject>? Objects(TeModel m, string scope) => scope switch
    {
        "Model" => [m],
        "Table" => m.Tables.Where(t => !t.IsCalculatedTable && !t.IsCalculationGroup),
        "CalculatedTable" => m.Tables.Where(t => t.IsCalculatedTable),
        "CalculationGroup" => m.Tables.Where(t => t.IsCalculationGroup),
        "DataColumn" => m.AllColumns.Where(c => c.Type == "Data"),
        "CalculatedColumn" => m.AllColumns.Where(c => c.Type == "Calculated"),
        "CalculatedTableColumn" => m.AllColumns.Where(c => c.Type == "CalculatedTableColumn"),
        "Measure" => m.AllMeasures,
        "KPI" => m.AllMeasures.Where(x => x.KPI is not null).Select(x => (TeObject)x.KPI!),
        "Hierarchy" => m.AllHierarchies,
        "Level" => m.AllLevels,
        "Partition" => m.AllPartitions,
        "Relationship" => m.Relationships,
        "Perspective" => m.Perspectives,
        "Culture" => m.Cultures,
        "ModelRole" => m.Roles,
        "TablePermission" => m.Roles.SelectMany(r => r.TablePermissions),
        "CalculationItem" => m.AllCalculationItems,
        "NamedExpression" => m.Expressions,
        "ProviderDataSource" => m.DataSources.Where(d => d.Type == "Provider"),
        "StructuredDataSource" => m.DataSources.Where(d => d.Type == "Structured"),
        "Function" => m.Functions,
        _ => null,
    };
}
