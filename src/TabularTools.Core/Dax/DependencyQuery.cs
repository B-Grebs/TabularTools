using System.Text.Json.Serialization;
using TabularTools.Core.Snapshots;

namespace TabularTools.Core.Dax;

[JsonConverter(typeof(JsonStringEnumConverter<DependencyDirection>))]
public enum DependencyDirection
{
    [JsonStringEnumMemberName("upstream")] Upstream,
    [JsonStringEnumMemberName("downstream")] Downstream,
    [JsonStringEnumMemberName("both")] Both,
}

public sealed record ObjectRef(string Key, string Type, string Name, string? Table = null);

/// <summary>One edge: <paramref name="From"/> uses <paramref name="To"/>.</summary>
public sealed record DependencyEdge(
    ObjectRef From,
    ObjectRef To,
    string Via,
    int Depth,
    bool? FullyQualified = null,
    ExpressionPart? Part = null);

public sealed record UnresolvedReference(string Text, ExpressionPart Part, int Line, int Column);

public sealed record DependencyResult(
    ObjectRef Object,
    IReadOnlyList<DependencyEdge> Upstream,
    IReadOnlyList<DependencyEdge> Downstream,
    IReadOnlyList<UnresolvedReference> Unresolved,
    bool Truncated = false);

/// <summary>Answers "what does X depend on, and what depends on X?"</summary>
public static class DependencyQuery
{
    public const int MaxEdges = 2000;

    public static DependencyResult Query(ModelSnapshot model, DependencyGraph graph, string objectSpec,
        DependencyDirection direction = DependencyDirection.Both, int depth = 1)
    {
        var key = ResolveObject(model, graph, objectSpec);
        var maxDepth = depth <= 0 ? int.MaxValue : depth;
        var truncated = false;

        var upstream = direction is DependencyDirection.Upstream or DependencyDirection.Both
            ? Walk(key, maxDepth, k => graph.DependenciesOf(k).Where(r => r.IsResolved)
                .Select(r => (Next: r.Target!, Edge: (Func<int, DependencyEdge>)(d => new DependencyEdge(Ref(graph, k), Ref(graph, r.Target!), "dax", d, r.FullyQualified, r.Part)))), ref truncated)
            : [];

        var downstream = direction is DependencyDirection.Downstream or DependencyDirection.Both
            ? Walk(key, maxDepth, k =>
                graph.ReferencesTo(k).Select(r => (Next: r.Owner, Edge: (Func<int, DependencyEdge>)(d => new DependencyEdge(Ref(graph, r.Owner), Ref(graph, k), "dax", d, r.FullyQualified, r.Part))))
                .Concat(graph.StructuralUsesOf(k).Select(s => (Next: s.User, Edge: (Func<int, DependencyEdge>)(d => new DependencyEdge(Ref(graph, s.User), Ref(graph, k), Via(s.Kind), d))))), ref truncated)
            : [];

        var unresolved = graph.DependenciesOf(key).Where(r => !r.IsResolved)
            .Select(r => new UnresolvedReference(r.Text, r.Part, r.Line, r.Column)).ToList();

        return new DependencyResult(Ref(graph, key), upstream, downstream, unresolved, truncated);
    }

    private static List<DependencyEdge> Walk(string start, int maxDepth, Func<string, IEnumerable<(string Next, Func<int, DependencyEdge> Edge)>> step, ref bool truncated)
    {
        var edges = new List<DependencyEdge>();
        var seenEdges = new HashSet<(string, string, string)>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { start };
        var frontier = new List<string> { start };
        for (var d = 1; d <= maxDepth && frontier.Count > 0; d++)
        {
            var next = new List<string>();
            foreach (var key in frontier)
            {
                foreach (var (target, make) in step(key))
                {
                    var edge = make(d);
                    if (!seenEdges.Add((edge.From.Key, edge.To.Key, edge.Via))) continue;
                    if (edges.Count >= MaxEdges) { truncated = true; return edges; }
                    edges.Add(edge);
                    if (visited.Add(target)) next.Add(target);
                }
            }
            frontier = next;
        }
        return edges;
    }

    private static string Via(StructuralUseKind kind) => kind switch
    {
        StructuralUseKind.Relationship => "relationship",
        StructuralUseKind.SortBy => "sortBy",
        _ => "hierarchy",
    };

    /// <summary>
    /// Accepts an object key (<c>table:Sales/measure:Total Sales</c>), DAX-style references (<c>Sales[Amount]</c>,
    /// <c>'Sales'[Amount]</c>, <c>[Total Sales]</c>, <c>'Date'</c>), or a bare table, measure or function name.
    /// </summary>
    public static string ResolveObject(ModelSnapshot model, DependencyGraph graph, string spec)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spec);
        spec = spec.Trim();
        if (graph.Contains(spec)) return spec;
        if (spec.Contains(':', StringComparison.Ordinal) && !spec.StartsWith('\'') && !spec.StartsWith('['))
            throw new ArgumentException($"No object with key '{spec}'.");

        var tokens = DaxLexer.Significant(spec);
        if (tokens.Count == 2 && tokens[0].Type is DaxTokenType.TABLE or DaxTokenType.TABLE_OR_VARIABLE && tokens[1].Type == DaxTokenType.COLUMN_OR_MEASURE)
        {
            var table = model.Tables.FirstOrDefault(t => Eq(t.Name, tokens[0].Value))
                ?? throw new ArgumentException($"No table named '{tokens[0].Value}'.");
            var column = table.Columns.FirstOrDefault(c => Eq(c.Name, tokens[1].Value));
            if (column is not null) return column.Key;
            var measure = table.Measures.FirstOrDefault(m => Eq(m.Name, tokens[1].Value));
            if (measure is not null) return measure.Key;
            throw new ArgumentException($"Table '{table.Name}' has no column or measure named '{tokens[1].Value}'.");
        }

        if (tokens.Count == 1 && tokens[0].Type == DaxTokenType.COLUMN_OR_MEASURE)
        {
            var name = tokens[0].Value;
            var measure = model.AllMeasures.FirstOrDefault(m => Eq(m.Name, name));
            if (measure is not null) return measure.Key;
            var columns = model.AllColumns.Where(c => Eq(c.Name, name)).ToList();
            if (columns.Count == 1) return columns[0].Key;
            if (columns.Count > 1)
                throw new ArgumentException($"[{name}] is ambiguous; qualify it with its table: {string.Join(", ", columns.Select(c => $"'{c.Table}'[{c.Name}]"))}.");
            throw new ArgumentException($"No measure or column named [{name}].");
        }

        var bare = tokens.Count == 1 ? tokens[0].Value : spec;
        var t2 = model.Tables.FirstOrDefault(t => Eq(t.Name, bare));
        if (t2 is not null) return t2.Key;
        var m2 = model.AllMeasures.FirstOrDefault(m => Eq(m.Name, bare));
        if (m2 is not null) return m2.Key;
        var f2 = model.Functions.FirstOrDefault(f => Eq(f.Name, bare));
        if (f2 is not null) return f2.Key;
        throw new ArgumentException($"Could not find '{spec}'. Use Table[Column], [Measure], 'Table', or an object key from describe_model.");
    }

    internal static ObjectRef Ref(DependencyGraph graph, string key)
    {
        var type = graph.ObjectTypes.TryGetValue(key, out var t) ? t : "unknown";
        var (table, name) = SplitKey(key);
        return new ObjectRef(key, type, name, table);
    }

    /// <summary>Splits an object key into (table, last name segment).</summary>
    internal static (string? Table, string Name) SplitKey(string key)
    {
        if (key.StartsWith("relationship:", StringComparison.Ordinal)) return (null, key["relationship:".Length..]);
        var segments = key.Split('/');
        string? table = segments[0].StartsWith("table:", StringComparison.Ordinal) && segments.Length > 1 ? segments[0]["table:".Length..] : null;
        var last = segments[^1];
        var colon = last.IndexOf(':', StringComparison.Ordinal);
        return (table, colon >= 0 ? last[(colon + 1)..] : last);
    }

    private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
