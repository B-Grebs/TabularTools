using TabularTools.Core.Dax;
using TabularTools.Core.Diff;
using TabularTools.Core.Snapshots;

namespace TabularTools.Core.Testing;

/// <summary>A query-scoped definition: <c>DEFINE MEASURE 'Table'[Name] = Expression</c> (or COLUMN).</summary>
/// <param name="Key">Object key on its own side.</param>
public sealed record QueryDefinition(string Key, string Table, string Name, string Expression);

/// <summary>The definitions one side's queries override.</summary>
public sealed record SideDefinitions(IReadOnlyList<QueryDefinition> Measures, IReadOnlyList<QueryDefinition> Columns)
{
    public static SideDefinitions Empty { get; } = new([], []);
    public bool IsEmpty => Measures.Count == 0 && Columns.Count == 0;
}

/// <summary>
/// What differential tests override on each side (ARCHITECTURE §11.2), worked out from the matched base and head models:
/// measures whose DAX changed (formatting-only changes don't count), renamed measures (so each side's queries can name
/// them), added measures (head only) and removed measures (base only). Measures that only moved to another table keep
/// their model definition, because the table a measure sits in doesn't change its value. Dependents aren't overridden:
/// the engine evaluates them with the overridden definitions. Each query only defines what it depends on (see
/// <see cref="BaseFor"/>), so an unrelated change, such as a new measure on a new table, can't break it.
/// </summary>
public sealed record OverridePlan
{
    public required SideDefinitions Base { get; init; }
    public required SideDefinitions Head { get; init; }
    /// <summary>Head keys of measures whose DAX the change edited: a value change on these is expected.</summary>
    public required IReadOnlySet<string> DirectlyEdited { get; init; }
    /// <summary>Head keys of measures that don't exist in base.</summary>
    public required IReadOnlySet<string> Added { get; init; }
    /// <summary>Head measure key → the measure's name in base, for every measure in both.</summary>
    public required IReadOnlyDictionary<string, string> BaseNames { get; init; }
    /// <summary>Changes that can affect values but can't be simulated in a query (relationships, data types, RLS, …).</summary>
    public required IReadOnlyList<Change> StructuralChangesNotTested { get; init; }
    /// <summary>True when there's no base to compare with (no git, or the model is new).</summary>
    public bool NoBase { get; init; }
    /// <summary>Head measure key → base measure key, for every measure in both.</summary>
    public IReadOnlyDictionary<string, string> BaseKeys { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
    public DependencyGraph? BaseGraph { get; init; }
    public DependencyGraph? HeadGraph { get; init; }

    /// <summary>The base definitions a query evaluating <paramref name="baseRoots"/> (base keys) depends on.</summary>
    public SideDefinitions BaseFor(IEnumerable<string> baseRoots) => Needed(Base, BaseGraph, baseRoots);

    /// <summary>The head definitions a query evaluating <paramref name="headRoots"/> (head keys) depends on.</summary>
    public SideDefinitions HeadFor(IEnumerable<string> headRoots) => Needed(Head, HeadGraph, headRoots);

    private static SideDefinitions Needed(SideDefinitions all, DependencyGraph? graph, IEnumerable<string> roots)
    {
        if (all.IsEmpty || graph is null) return SideDefinitions.Empty;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>(roots);
        while (queue.Count > 0)
        {
            var key = queue.Dequeue();
            if (!seen.Add(key)) continue;
            foreach (var r in graph.DependenciesOf(key))
                if (r.Target is { } target && !seen.Contains(target)) queue.Enqueue(target);
        }
        return new SideDefinitions(all.Measures.Where(d => seen.Contains(d.Key)).ToList(), all.Columns.Where(d => seen.Contains(d.Key)).ToList());
    }

    /// <summary>A plan for a model with no base: nothing overridden, every measure "added".</summary>
    public static OverridePlan HeadOnly(ModelSnapshot head) => new()
    {
        Base = SideDefinitions.Empty,
        Head = SideDefinitions.Empty,
        DirectlyEdited = new HashSet<string>(StringComparer.Ordinal),
        Added = head.AllMeasures.Select(m => m.Key).ToHashSet(StringComparer.Ordinal),
        BaseNames = new Dictionary<string, string>(StringComparer.Ordinal),
        StructuralChangesNotTested = [],
        NoBase = true,
    };

    public static OverridePlan Build(ModelSnapshot @base, ModelSnapshot head, ModelMatch match, IReadOnlyList<Change> changes, bool columnOverrides,
        DependencyGraph? baseGraph = null, DependencyGraph? headGraph = null)
    {
        ArgumentNullException.ThrowIfNull(match);
        var baseMeasures = new List<QueryDefinition>();
        var headMeasures = new List<QueryDefinition>();
        var edited = new HashSet<string>(StringComparer.Ordinal);
        var baseNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var baseKeys = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var pair in match.Measures.Pairs)
        {
            var (b, h) = (pair.Base, pair.Head);
            baseNames[h.Key] = b.Name;
            baseKeys[h.Key] = b.Key;
            var exprChanged = DaxLexer.Normalize(b.Expression) != DaxLexer.Normalize(h.Expression);
            var renamed = !b.Name.Equals(h.Name, StringComparison.OrdinalIgnoreCase);
            if (exprChanged) edited.Add(h.Key);
            if (exprChanged || renamed)
            {
                baseMeasures.Add(new QueryDefinition(b.Key, b.Table, b.Name, b.Expression));
                headMeasures.Add(new QueryDefinition(h.Key, h.Table, h.Name, h.Expression));
            }
        }
        foreach (var m in match.Measures.Added) headMeasures.Add(new QueryDefinition(m.Key, m.Table, m.Name, m.Expression));
        foreach (var m in match.Measures.Removed) baseMeasures.Add(new QueryDefinition(m.Key, m.Table, m.Name, m.Expression));

        var baseColumns = new List<QueryDefinition>();
        var headColumns = new List<QueryDefinition>();
        if (columnOverrides)
        {
            foreach (var pair in match.TableChildren.SelectMany(t => t.Columns.Pairs))
            {
                var (b, h) = (pair.Base, pair.Head);
                if (b.Kind != ColumnKind.Calculated || h.Kind != ColumnKind.Calculated || pair.Rename is not null) continue;
                if (DaxLexer.Normalize(b.Expression) == DaxLexer.Normalize(h.Expression)) continue;
                baseColumns.Add(new QueryDefinition(b.Key, b.Table, b.Name, b.Expression ?? ""));
                headColumns.Add(new QueryDefinition(h.Key, h.Table, h.Name, h.Expression ?? ""));
            }
        }

        var simulatedColumns = headColumns.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
        var structural = changes
            .Where(c => TestCoverage.Classify(c.Kind) == ChangeCoverage.NotSimulated)
            .Where(c => !(c.Kind == "column.expressionChanged" && simulatedColumns.Contains(c.Object.Key)))
            .ToList();

        return new OverridePlan
        {
            Base = new SideDefinitions(Ordered(baseMeasures, @base), baseColumns),
            Head = new SideDefinitions(Ordered(headMeasures, head), headColumns),
            DirectlyEdited = edited,
            Added = match.Measures.Added.Select(m => m.Key).ToHashSet(StringComparer.Ordinal),
            BaseNames = baseNames,
            BaseKeys = baseKeys,
            BaseGraph = baseGraph ?? DependencyAnalyzer.Analyze(@base),
            HeadGraph = headGraph ?? DependencyAnalyzer.Analyze(head),
            StructuralChangesNotTested = structural,
        };
    }

    /// <summary>Model order (table, then measure), so queries are stable whatever order the matcher used.</summary>
    private static List<QueryDefinition> Ordered(List<QueryDefinition> defs, ModelSnapshot model)
    {
        var order = model.AllMeasures.Select((m, i) => (m.Key, i)).ToDictionary(x => x.Key, x => x.i, StringComparer.Ordinal);
        return defs.OrderBy(d => order.TryGetValue(d.Key, out var i) ? i : int.MaxValue)
            .ThenBy(d => d.Table, StringComparer.Ordinal).ThenBy(d => d.Name, StringComparer.Ordinal).ToList();
    }
}

public enum ChangeCoverage
{
    /// <summary>Differential tests simulate it with query-scoped definitions.</summary>
    Simulated,
    /// <summary>Can change values but can't be expressed in a query: listed as not tested.</summary>
    NotSimulated,
    /// <summary>Doesn't change query results (names shown in reports, formatting, metadata).</summary>
    NoValueEffect,
}

/// <summary>Which diff change kinds measure tests cover. Every kind in <see cref="SeverityPolicy"/> is classified.</summary>
public static class TestCoverage
{
    private static readonly HashSet<string> Simulated = new(StringComparer.Ordinal)
    {
        "measure.added", "measure.removed", "measure.renamed", "measure.moved", "measure.expressionChanged",
    };

    private static readonly HashSet<string> NotSimulated = new(StringComparer.Ordinal)
    {
        "model.defaultModeChanged",
        "table.removed", "table.renamed", "table.kindChanged", "table.expressionChanged",
        "column.removed", "column.renamed", "column.kindChanged", "column.dataTypeChanged", "column.keyDataTypeChanged",
        "column.expressionChanged", "column.sourceColumnChanged",
        "partition.added", "partition.removed", "partition.sourceChanged", "partition.sourceTypeChanged", "partition.modeChanged",
        "calculationItem.removed", "calculationItem.renamed", "calculationItem.expressionChanged", "calculationItem.ordinalChanged",
        "calculationGroup.precedenceChanged",
        "relationship.added", "relationship.removed", "relationship.crossFilterChanged", "relationship.cardinalityChanged",
        "relationship.activeChanged", "relationship.securityFilteringChanged", "relationship.referentialIntegrityChanged",
        "role.removed", "role.modelPermissionChanged", "role.rlsFilterAdded", "role.rlsFilterChanged", "role.rlsFilterRemoved", "role.olsChanged",
        "expression.removed", "expression.renamed", "expression.expressionChanged", "expression.kindChanged",
        "function.removed", "function.renamed", "function.expressionChanged",
    };

    public static ChangeCoverage Classify(string kind) =>
        Simulated.Contains(kind) ? ChangeCoverage.Simulated
        : NotSimulated.Contains(kind) ? ChangeCoverage.NotSimulated
        : ChangeCoverage.NoValueEffect;

    /// <summary>Kinds listed explicitly (the rest are <see cref="ChangeCoverage.NoValueEffect"/>); used by a test that checks the lists are current.</summary>
    internal static IEnumerable<string> ExplicitKinds => Simulated.Concat(NotSimulated);
}
