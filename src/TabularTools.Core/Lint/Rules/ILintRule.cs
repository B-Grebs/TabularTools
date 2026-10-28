using TabularTools.Core.Dax;
using TabularTools.Core.Lint.Compat;
using TabularTools.Core.Loading;

namespace TabularTools.Core.Lint.Rules;

/// <summary>A lint rule: built-in C# or a compiled BPA expression.</summary>
public interface ILintRule
{
    RuleInfo Info { get; }

    RuleEvaluation Evaluate(LintContext context);
}

/// <summary>An object a rule flagged, with optional detail for the message.</summary>
public sealed record RuleHit(ObjectRef Object, string? Detail = null);

/// <summary>Outcome of running one rule: hits, or the reason it couldn't run, plus any per-object errors.</summary>
public sealed record RuleEvaluation(IReadOnlyList<RuleHit> Hits, string? SkipReason = null, int ErrorCount = 0, string? FirstError = null)
{
    public static RuleEvaluation Skipped(string reason) => new([], reason);
}

/// <summary>Everything a rule may look at. The compatibility view is built on first use.</summary>
public sealed class LintContext
{
    private readonly Lazy<TeModel> _view;

    public LintContext(LoadedModel model)
    {
        Model = model;
        _view = new Lazy<TeModel>(() => new TeModel(model.Database, model.Dependencies));
    }

    public LoadedModel Model { get; }

    public DependencyGraph Graph => Model.Dependencies;

    public TeModel View => _view.Value;

    public static ObjectRef RefOf(TeObject o)
    {
        var type = o.ObjectType switch
        {
            "ModelRole" => "role",
            "NamedExpression" => "expression",
            "ProviderDataSource" or "StructuredDataSource" => "dataSource",
            "KPI" => "kpi",
            var t => char.ToLowerInvariant(t[0]) + t[1..],
        };
        string? table = o switch
        {
            TeColumn c => c.Table.Name,
            TeMeasure m => m.Table.Name,
            TeHierarchy h => h.Table.Name,
            TeLevel l => l.Table.Name,
            TePartition p => p.Table.Name,
            TeCalculationItem ci => ci.Table.Name,
            TeKpi k => k.Table.Name,
            TeTablePermission tp => tp.Table.Name,
            _ => null,
        };
        return new ObjectRef(o.Key, type, o.Name, table);
    }
}
