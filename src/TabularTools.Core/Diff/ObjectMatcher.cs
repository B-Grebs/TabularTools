using TabularTools.Core.Dax;
using TabularTools.Core.Snapshots;

namespace TabularTools.Core.Diff;

/// <summary>A base object and the head object it became. <see cref="Rename"/> is set when the name changed.</summary>
public sealed record MatchedPair<T>(T Base, T Head, RenameInfo? Rename);

public sealed record MatchSet<T>(IReadOnlyList<MatchedPair<T>> Pairs, IReadOnlyList<T> Removed, IReadOnlyList<T> Added);

/// <summary>Every object of two model versions, matched (ARCHITECTURE §10.3).</summary>
public sealed class ModelMatch
{
    public required MatchSet<TableSnapshot> Tables { get; init; }
    public required MatchSet<MeasureSnapshot> Measures { get; init; }
    /// <summary>Columns, partitions, hierarchies and calculation items, matched within each matched table pair.</summary>
    public required IReadOnlyList<TableChildren> TableChildren { get; init; }
    public required MatchSet<RelationshipSnapshot> Relationships { get; init; }
    public required MatchSet<RoleSnapshot> Roles { get; init; }
    public required MatchSet<NamedExpressionSnapshot> Expressions { get; init; }
    public required MatchSet<FunctionSnapshot> Functions { get; init; }

    /// <summary>Base object key → head object key, for every matched object.</summary>
    public required IReadOnlyDictionary<string, string> KeyMap { get; init; }
}

public sealed record TableChildren(
    MatchedPair<TableSnapshot> Table,
    MatchSet<ColumnSnapshot> Columns,
    MatchSet<PartitionSnapshot> Partitions,
    MatchSet<HierarchySnapshot> Hierarchies,
    MatchSet<CalculationItemSnapshot> CalculationItems);

public static class ObjectMatcher
{
    public const double DefaultRenameThreshold = 0.8;

    /// <summary>Heuristic matching compares at most this many unmatched objects per side, per type and scope.</summary>
    public const int MaxHeuristicCandidates = 200;

    public static ModelMatch Match(ModelSnapshot @base, ModelSnapshot head, double renameThreshold = DefaultRenameThreshold)
    {
        var tables = MatchObjects(@base.Tables, head.Tables, t => t.Name, t => t.LineageTag, TableScore, renameThreshold);
        var measures = MatchObjects(@base.AllMeasures.ToList(), head.AllMeasures.ToList(), m => m.Name, m => m.LineageTag,
            (a, b) => ExpressionScore(a.Expression, b.Expression, [(a.FormatString, b.FormatString), (a.DisplayFolder, b.DisplayFolder)]), renameThreshold,
            heuristicScope: m => m.Table);

        var children = tables.Pairs.Select(pair => new TableChildren(
                pair,
                MatchObjects(pair.Base.Columns, pair.Head.Columns, c => c.Name, c => c.LineageTag, ColumnScore, renameThreshold),
                MatchObjects(pair.Base.Partitions, pair.Head.Partitions, p => p.Name, _ => null, null, renameThreshold),
                MatchObjects(pair.Base.Hierarchies, pair.Head.Hierarchies, h => h.Name, h => h.LineageTag, HierarchyScore, renameThreshold),
                MatchObjects(pair.Base.CalculationItems, pair.Head.CalculationItems, c => c.Name, _ => null,
                    (a, b) => ExpressionScore(a.Expression, b.Expression, [(a.FormatStringExpression, b.FormatStringExpression)]), renameThreshold)))
            .ToList();

        // Relationships are identified by their endpoints, following table and column renames.
        var tableMap = tables.Pairs.ToDictionary(p => p.Base.Name, p => p.Head.Name, StringComparer.OrdinalIgnoreCase);
        var columnMap = children.SelectMany(c => c.Columns.Pairs.Select(p => (c.Table.Base.Name, p.Base.Name, p.Head.Name)))
            .ToDictionary(x => (x.Item1.ToUpperInvariant(), x.Item2.ToUpperInvariant()), x => x.Item3);
        string Endpoints(RelationshipSnapshot r, bool mapToHead)
        {
            if (!mapToHead) return EndpointKey(r.FromTable, r.FromColumn, r.ToTable, r.ToColumn);
            string T(string t) => tableMap.GetValueOrDefault(t, t);
            string C(string t, string c) => columnMap.GetValueOrDefault((t.ToUpperInvariant(), c.ToUpperInvariant()), c);
            return EndpointKey(T(r.FromTable), C(r.FromTable, r.FromColumn), T(r.ToTable), C(r.ToTable, r.ToColumn));
        }
        var headRels = head.Relationships.GroupBy(r => Endpoints(r, false)).ToDictionary(g => g.Key, g => g.ToList());
        var relPairs = new List<MatchedPair<RelationshipSnapshot>>();
        var relRemoved = new List<RelationshipSnapshot>();
        var usedHead = new HashSet<RelationshipSnapshot>(ReferenceEqualityComparer.Instance);
        foreach (var r in @base.Relationships)
        {
            if (headRels.TryGetValue(Endpoints(r, true), out var candidates) && candidates.FirstOrDefault(c => !usedHead.Contains(c)) is { } match)
            {
                usedHead.Add(match);
                relPairs.Add(new MatchedPair<RelationshipSnapshot>(r, match, null));
            }
            else relRemoved.Add(r);
        }
        var relationships = new MatchSet<RelationshipSnapshot>(relPairs, relRemoved, head.Relationships.Where(r => !usedHead.Contains(r)).ToList());

        var roles = MatchObjects(@base.Roles, head.Roles, r => r.Name, _ => null, null, renameThreshold);
        var expressions = MatchObjects(@base.Expressions, head.Expressions, e => e.Name, e => e.LineageTag,
            (a, b) => ExpressionScore(a.Expression, b.Expression, [], m: a.Kind == "m"), renameThreshold);
        var functions = MatchObjects(@base.Functions, head.Functions, f => f.Name, f => f.LineageTag,
            (a, b) => ExpressionScore(a.Expression, b.Expression, []), renameThreshold);

        var keyMap = new Dictionary<string, string>(StringComparer.Ordinal);
        void Map<T>(MatchSet<T> set, Func<T, string> key)
        {
            foreach (var p in set.Pairs) keyMap[key(p.Base)] = key(p.Head);
        }
        Map(tables, t => t.Key);
        Map(measures, m => m.Key);
        foreach (var c in children)
        {
            Map(c.Columns, x => x.Key);
            Map(c.Partitions, x => x.Key);
            Map(c.Hierarchies, x => x.Key);
            Map(c.CalculationItems, x => x.Key);
        }
        Map(relationships, r => r.Key);
        Map(roles, r => r.Key);
        foreach (var p in roles.Pairs)
            foreach (var tp in p.Base.TablePermissions)
                keyMap[ObjectKeys.TablePermission(p.Base.Name, tp.Table)] = ObjectKeys.TablePermission(p.Head.Name, tableMap.GetValueOrDefault(tp.Table, tp.Table));
        Map(expressions, e => e.Key);
        Map(functions, f => f.Key);
        keyMap[ObjectKeys.Model] = ObjectKeys.Model;

        return new ModelMatch
        {
            Tables = tables, Measures = measures, TableChildren = children, Relationships = relationships,
            Roles = roles, Expressions = expressions, Functions = functions, KeyMap = keyMap,
        };
    }

    private static string EndpointKey(string ft, string fc, string tt, string tc) =>
        $"{ft}\u0001{fc}\u0001{tt}\u0001{tc}".ToUpperInvariant();

    /// <summary>
    /// Matches by name (case-insensitive), then by lineage tag, then by a similarity score above the threshold.
    /// With <paramref name="heuristicScope"/>, heuristic candidates must share that scope (e.g. the same table).
    /// </summary>
    internal static MatchSet<T> MatchObjects<T>(
        IReadOnlyList<T> @base, IReadOnlyList<T> head,
        Func<T, string> name, Func<T, string?> lineageTag,
        Func<T, T, (double Score, string Evidence)>? score, double threshold,
        Func<T, string>? heuristicScope = null) where T : class
    {
        var pairs = new List<MatchedPair<T>>();
        var headByName = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in head) headByName.TryAdd(name(h), h);
        var unmatchedBase = new List<T>();
        var used = new HashSet<T>(ReferenceEqualityComparer.Instance);

        // 1. Same name.
        foreach (var b in @base)
        {
            if (headByName.TryGetValue(name(b), out var h) && used.Add(h))
            {
                var rename = string.Equals(name(b), name(h), StringComparison.Ordinal) ? null : new RenameInfo(name(b), name(h), "certain", "name");
                pairs.Add(new MatchedPair<T>(b, h, rename));
            }
            else unmatchedBase.Add(b);
        }

        // 2. Same lineage tag.
        var headByTag = head.Where(h => !used.Contains(h) && !string.IsNullOrEmpty(lineageTag(h)))
            .GroupBy(h => lineageTag(h)!, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var stillUnmatched = new List<T>();
        foreach (var b in unmatchedBase)
        {
            if (lineageTag(b) is { Length: > 0 } tag && headByTag.TryGetValue(tag, out var h) && used.Add(h))
                pairs.Add(new MatchedPair<T>(b, h, new RenameInfo(name(b), name(h), "certain", "lineageTag")));
            else stillUnmatched.Add(b);
        }

        // 3. Heuristic: best-scoring pairs above the threshold, greedily.
        if (score is not null && stillUnmatched.Count > 0)
        {
            var candidatesHead = head.Where(h => !used.Contains(h)).ToList();
            var scopes = heuristicScope is null
                ? [(stillUnmatched, candidatesHead)]
                : stillUnmatched.GroupBy(heuristicScope, StringComparer.OrdinalIgnoreCase)
                    .Select(g => (g.ToList(), candidatesHead.Where(h => string.Equals(heuristicScope(h), g.Key, StringComparison.OrdinalIgnoreCase)).ToList()))
                    .ToList();

            var scored = new List<(T B, T H, double Score, string Evidence)>();
            foreach (var (bs, hs) in scopes)
                foreach (var b in bs.Take(MaxHeuristicCandidates))
                    foreach (var h in hs.Take(MaxHeuristicCandidates))
                    {
                        var (s, evidence) = score(b, h);
                        if (s >= threshold) scored.Add((b, h, s, evidence));
                    }

            var matchedBase = new HashSet<T>(ReferenceEqualityComparer.Instance);
            foreach (var (b, h, s, evidence) in scored.OrderByDescending(x => x.Score).ThenBy(x => name(x.B), StringComparer.Ordinal).ThenBy(x => name(x.H), StringComparer.Ordinal))
            {
                if (matchedBase.Contains(b) || used.Contains(h)) continue;
                matchedBase.Add(b);
                used.Add(h);
                pairs.Add(new MatchedPair<T>(b, h, new RenameInfo(name(b), name(h), s >= 0.9 ? "high" : "medium", evidence, Math.Round(s, 3))));
            }
            stillUnmatched = stillUnmatched.Where(b => !matchedBase.Contains(b)).ToList();
        }

        return new MatchSet<T>(pairs, stillUnmatched, head.Where(h => !used.Contains(h)).ToList());
    }

    // ---- Scores (ARCHITECTURE §10.3) ----

    internal static (double, string) ExpressionScore(string? a, string? b, IReadOnlyList<(string? A, string? B)> properties, bool m = false)
    {
        var ta = Tokens(a, m);
        var tb = Tokens(b, m);
        if (ta.Count == 0 && tb.Count == 0) return (0, "expression");
        if (ta.SequenceEqual(tb, StringComparer.Ordinal)) return (0.95, "expression");
        var props = properties.Count == 0 ? 0 : properties.Count(p => string.Equals(p.A ?? "", p.B ?? "", StringComparison.Ordinal)) / (double)properties.Count;
        var weight = properties.Count == 0 ? 1.0 : 0.7;
        return (TextDiff.TokenSimilarity(ta, tb) * weight + props * (1 - weight), "expression");
    }

    private static (double, string) ColumnScore(ColumnSnapshot a, ColumnSnapshot b)
    {
        if (a.Kind != b.Kind) return (0, "");
        if (a.Kind == ColumnKind.Calculated)
            return ExpressionScore(a.Expression, b.Expression, [(a.DataType, b.DataType), (a.FormatString, b.FormatString), (a.DisplayFolder, b.DisplayFolder)]);
        if (!string.IsNullOrEmpty(a.SourceColumn) && string.Equals(a.SourceColumn, b.SourceColumn, StringComparison.OrdinalIgnoreCase))
            return a.DataType == b.DataType ? (0.9, "sourceColumn") : (0.7, "sourceColumn");
        return (0, "");
    }

    private static (double, string) TableScore(TableSnapshot a, TableSnapshot b)
    {
        var ca = a.Columns.Select(c => (c.Name.ToUpperInvariant(), c.DataType)).ToHashSet();
        var cb = b.Columns.Select(c => (c.Name.ToUpperInvariant(), c.DataType)).ToHashSet();
        var union = ca.Union(cb).Count();
        var jaccard = union == 0 ? 0 : ca.Intersect(cb).Count() / (double)union;
        var sa = Tokens(string.Join("\n", a.Partitions.Select(p => p.Expression)), m: true);
        var sb = Tokens(string.Join("\n", b.Partitions.Select(p => p.Expression)), m: true);
        var source = sa.Count == 0 && sb.Count == 0 ? 1 : TextDiff.TokenSimilarity(sa, sb);
        return (jaccard * 0.7 + source * 0.3, "columns");
    }

    private static (double, string) HierarchyScore(HierarchySnapshot a, HierarchySnapshot b) =>
        a.Levels.Count > 0 && a.Levels.Select(l => l.Column).SequenceEqual(b.Levels.Select(l => l.Column), StringComparer.OrdinalIgnoreCase)
            ? (0.9, "levels") : (0, "");

    /// <summary>Significant tokens of an expression. DAX keywords and functions compare case-insensitively; M is case-sensitive.</summary>
    internal static List<string> Tokens(string? expression, bool m) =>
        DaxLexer.Significant(expression).Select(t => !m && t.Type is DaxTokenType.FUNCTION or DaxTokenType.KEYWORD or DaxTokenType.VAR
            or DaxTokenType.RETURN or DaxTokenType.IN or DaxTokenType.NOT or DaxTokenType.TRUE or DaxTokenType.FALSE ? t.Text.ToUpperInvariant() : t.Text).ToList();

    /// <summary>The expression reduced to significant tokens, for formatting-only comparisons.</summary>
    internal static string Normalize(string? expression, bool m) => string.Join(' ', Tokens(expression, m));
}
