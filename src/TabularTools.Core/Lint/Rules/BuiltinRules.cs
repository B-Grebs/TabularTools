using System.Text.RegularExpressions;
using TabularTools.Core.Dax;
using TabularTools.Core.Snapshots;

namespace TabularTools.Core.Lint.Rules;

/// <summary>Checks that BPA expressions can't express well. IDs start with TT.</summary>
public static partial class BuiltinRules
{
    public static IReadOnlyList<ILintRule> All { get; } =
    [
        new DelegateRule(Info("TT001", "DAX expression references an object that doesn't exist", "Error Prevention", Severity.Error,
            "A measure, calculated column or table, calculation item, RLS filter or function refers to a table, column or measure " +
            "that isn't in the model. The expression will fail. Fix the reference or restore the object.",
            "Measure", "CalculatedColumn", "CalculatedTable", "CalculationItem", "TablePermission", "Function"), BrokenReferences),
        new DelegateRule(Info("TT002", "Bidirectional relationship creates ambiguous filter paths", "Error Prevention", Severity.Warning,
            "With this relationship filtering in both directions, a filter can reach some table along more than one path. " +
            "Results then depend on which path the engine picks. Make the relationship single-direction and use CROSSFILTER in the measures that need it.",
            "Relationship"), AmbiguousPaths),
        new DelegateRule(Info("TT003", "RLS filter uses a column from another table without qualifying it", "Error Prevention", Severity.Error,
            "An unqualified [Column] in a role's table filter is evaluated in the secured table's row context. " +
            "This filter names a column that isn't in the secured table. Qualify the reference or move the filter to the right table.",
            "TablePermission"), RlsUnqualifiedForeignColumn),
        new DelegateRule(Info("TT004", "Data source server or database is hard-coded", "Maintenance", Severity.Info,
            "The Power Query expression passes a literal server or database name to a connector. Use M parameters so the same " +
            "model can point at dev, test and production sources without editing partitions.",
            "Partition", "NamedExpression"), HardCodedSources),
        new DelegateRule(Info("TT005", "Measure has the same name as a column", "Naming Conventions", Severity.Warning,
            "[Name] then means different things in different places, which makes DAX hard to read and easy to break. Rename the measure or the column.",
            "Measure"), MeasureColumnCollisions),
    ];

    private static RuleInfo Info(string id, string name, string category, Severity severity, string description, params string[] scope) => new()
    {
        Id = id, Name = name, Ruleset = RulesetLoader.Builtin, Category = category, Severity = severity, Description = description, Scope = scope,
        HelpUrl = $"https://github.com/B-Grebs/TabularTools/blob/main/docs/rules/builtin.md#{id.ToLowerInvariant()}",
    };

    private static IEnumerable<RuleHit> BrokenReferences(LintContext ctx) =>
        ctx.Graph.Unresolved
            .GroupBy(r => r.Owner)
            .Select(g => new RuleHit(DependencyQuery.Ref(ctx.Graph, g.Key),
                "unknown: " + string.Join(", ", g.Select(r => $"{r.Text} ({Part(r.Part)}line {r.Line})").Distinct())));

    private static IEnumerable<RuleHit> RlsUnqualifiedForeignColumn(LintContext ctx) =>
        ctx.Graph.References
            .Where(r => ctx.Graph.ObjectTypes.GetValueOrDefault(r.Owner) == "tablePermission"
                        && r is { Kind: ReferenceKind.Column, FullyQualified: false, IsResolved: true }
                        && !string.Equals(r.Table, DependencyQuery.SplitKey(r.Owner).Name, StringComparison.OrdinalIgnoreCase))
            .GroupBy(r => r.Owner)
            .Select(g => new RuleHit(DependencyQuery.Ref(ctx.Graph, g.Key), string.Join(", ", g.Select(r => $"{r.Text} is in '{r.Table}'").Distinct())));

    private static IEnumerable<RuleHit> MeasureColumnCollisions(LintContext ctx)
    {
        var columns = ctx.Model.Snapshot.AllColumns.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(c => $"'{c.Table}'[{c.Name}]").ToList(), StringComparer.OrdinalIgnoreCase);
        foreach (var m in ctx.Model.Snapshot.AllMeasures)
            if (columns.TryGetValue(m.Name, out var cols))
                yield return new RuleHit(DependencyQuery.Ref(ctx.Graph, m.Key), "also a column: " + string.Join(", ", cols));
    }

    [GeneratedRegex(@"\b(Sql\.Databases?|AnalysisServices\.Databases?|Oracle\.Database|PostgreSQL\.Database|MySQL\.Database|Snowflake\.Databases|Databricks\.Catalogs|GoogleBigQuery\.Database|AmazonRedshift\.Database|Teradata\.Database|Sybase\.Database|DB2\.Database|Odbc\.DataSource|OleDb\.DataSource)\s*\(\s*""", RegexOptions.CultureInvariant)]
    private static partial Regex HardCodedConnector();

    private static IEnumerable<RuleHit> HardCodedSources(LintContext ctx)
    {
        var s = ctx.Model.Snapshot;
        foreach (var p in s.AllPartitions.Where(p => p.SourceType == "m" && p.Expression is not null))
            if (HardCodedConnector().Match(p.Expression!) is { Success: true } m)
                yield return new RuleHit(new ObjectRef(p.Key, "partition", p.Name, p.Table), $"{m.Groups[1].Value} with a literal argument");
        foreach (var e in s.Expressions.Where(e => e.Kind == "m" && !e.IsParameter))
            if (HardCodedConnector().Match(e.Expression) is { Success: true } m)
                yield return new RuleHit(new ObjectRef(e.Key, "expression", e.Name), $"{m.Groups[1].Value} with a literal argument");
    }

    /// <summary>
    /// Builds the filter-propagation graph of active relationships (one side filters many side; both directions when
    /// bidirectional or one-to-one) and reports bidirectional relationships used by a pair of tables with two or more paths.
    /// </summary>
    private static IEnumerable<RuleHit> AmbiguousPaths(LintContext ctx)
    {
        var rels = ctx.Model.Snapshot.Relationships.Where(r => r.IsActive).ToList();
        var edges = new Dictionary<string, List<(string To, RelationshipSnapshot Rel, bool Reverse)>>(StringComparer.OrdinalIgnoreCase);
        void Add(string from, string to, RelationshipSnapshot rel, bool reverse)
        {
            if (!edges.TryGetValue(from, out var list)) edges[from] = list = [];
            list.Add((to, rel, reverse));
        }
        foreach (var r in rels)
        {
            var oneToOne = r.FromCardinality == "one" && r.ToCardinality == "one";
            var manyToMany = r.FromCardinality == "many" && r.ToCardinality == "many";
            Add(r.ToTable, r.FromTable, r, false); // filters flow from the one side to the many side
            if (r.CrossFilteringBehavior == "bothDirections" || oneToOne || manyToMany) Add(r.FromTable, r.ToTable, r, true);
        }

        var flagged = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var source in edges.Keys.ToList())
        {
            var pathsTo = new Dictionary<string, List<List<(RelationshipSnapshot Rel, bool Reverse)>>>(StringComparer.OrdinalIgnoreCase);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { source };
            var budget = 5000;
            void Dfs(string node, List<(RelationshipSnapshot, bool)> path)
            {
                if (--budget < 0 || !edges.TryGetValue(node, out var outs)) return;
                foreach (var (to, rel, reverse) in outs)
                {
                    if (visited.Contains(to)) continue;
                    var next = new List<(RelationshipSnapshot, bool)>(path) { (rel, reverse) };
                    if (!pathsTo.TryGetValue(to, out var list)) pathsTo[to] = list = [];
                    if (list.Count < 4) list.Add(next);
                    visited.Add(to);
                    Dfs(to, next);
                    visited.Remove(to);
                }
            }
            Dfs(source, []);

            foreach (var (target, paths) in pathsTo.Where(p => p.Value.Count > 1))
                foreach (var (rel, _) in paths.SelectMany(p => p).Where(step => step.Reverse))
                    flagged.TryAdd(rel.Key, $"filters from '{source}' reach '{target}' along {paths.Count} paths");
        }

        return flagged.Select(f => new RuleHit(DependencyQuery.Ref(ctx.Graph, f.Key), f.Value));
    }

    private static string Part(ExpressionPart part) => part switch
    {
        ExpressionPart.FormatString => "format string, ",
        ExpressionPart.DetailRows => "detail rows, ",
        _ => "",
    };

    private sealed class DelegateRule(RuleInfo info, Func<LintContext, IEnumerable<RuleHit>> evaluate) : ILintRule
    {
        public RuleInfo Info { get; } = info;

        public RuleEvaluation Evaluate(LintContext context) => new(evaluate(context).ToList());
    }
}
