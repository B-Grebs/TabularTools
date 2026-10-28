using TabularTools.Core.Dax;
using TabularTools.Core.Snapshots;

namespace TabularTools.Core.Diff;

public sealed record DiffOptions
{
    /// <summary>Per-kind severity overrides from config.</summary>
    public IReadOnlyDictionary<string, ChangeSeverity>? SeverityOverrides { get; init; }
    public double RenameThreshold { get; init; } = ObjectMatcher.DefaultRenameThreshold;
    /// <summary>Report expression changes that only touch whitespace or comments.</summary>
    public bool IncludeFormattingOnly { get; init; }
    /// <summary>Drop changes below this severity.</summary>
    public ChangeSeverity? MinSeverity { get; init; }
}

/// <summary>One side of a model comparison.</summary>
/// <param name="Snapshot">The model, or null when it doesn't exist on this side.</param>
/// <param name="Dependencies">Its dependency graph (for impact), if available.</param>
/// <param name="Locations">Where its objects are declared.</param>
public sealed record DiffInput(ModelSnapshot? Snapshot, DependencyGraph? Dependencies = null, SourceLocationIndex? Locations = null);

public sealed record DiffOutcome(IReadOnlyList<Change> Changes, int FormattingOnlyHidden, ModelMatch Match);

/// <summary>Compares two snapshots of a model by meaning (ARCHITECTURE §10).</summary>
public static class DiffEngine
{
    public const int MaxImpactListed = 25;

    public static DiffOutcome Compare(DiffInput @base, DiffInput head, DiffOptions? options = null)
    {
        options ??= new DiffOptions();
        var b = @base.Snapshot ?? EmptyModel(head.Snapshot?.Name ?? "");
        var h = head.Snapshot ?? EmptyModel(b.Name);
        var match = ObjectMatcher.Match(b, h, options.RenameThreshold);
        var c = new Collector(@base, head, options);

        CompareModel(c, b, h);
        CompareTables(c, match, b, h);
        CompareMeasures(c, match);
        foreach (var children in match.TableChildren) CompareTableChildren(c, children, b, h);
        CompareRelationships(c, match);
        CompareRoles(c, match);
        CompareExpressions(c, match);
        CompareFunctions(c, match);

        var changes = c.Changes
            .Where(x => options.MinSeverity is not { } min || x.Severity >= min)
            .OrderByDescending(x => x.Severity)
            .ThenBy(x => x.Object.Key, StringComparer.Ordinal)
            .ThenBy(x => x.Kind, StringComparer.Ordinal)
            .ToList();
        return new DiffOutcome(changes, c.FormattingHidden, match);
    }

    public static ModelSnapshot EmptyModel(string name) => new(name, 0, null, null, false, [], [], [], [], [], [], []);

    // ---------------- Model ----------------

    private static void CompareModel(Collector c, ModelSnapshot b, ModelSnapshot h)
    {
        if (b.Tables.Count == 0 && b.CompatibilityLevel == 0 || h.Tables.Count == 0 && h.CompatibilityLevel == 0) return; // model added or removed
        var model = new ObjectRef(ObjectKeys.Model, "model", h.Name);
        c.Prop("model.compatibilityLevelChanged", model, b.CompatibilityLevel.ToString(System.Globalization.CultureInfo.InvariantCulture), h.CompatibilityLevel.ToString(System.Globalization.CultureInfo.InvariantCulture));
        c.Prop("model.discourageImplicitMeasuresChanged", model, b.DiscourageImplicitMeasures ? "true" : "false", h.DiscourageImplicitMeasures ? "true" : "false");
        c.Prop("model.defaultModeChanged", model, b.DefaultMode, h.DefaultMode);
        c.Prop("model.cultureChanged", model, b.Culture, h.Culture);

        foreach (var p in h.Perspectives.Except(b.Perspectives, StringComparer.OrdinalIgnoreCase))
            c.Add("perspective.added", new ObjectRef(ObjectKeys.Perspective(p), "perspective", p), after: p);
        foreach (var p in b.Perspectives.Except(h.Perspectives, StringComparer.OrdinalIgnoreCase))
            c.Add("perspective.removed", new ObjectRef(ObjectKeys.Perspective(p), "perspective", p), before: p, onBase: true);
        foreach (var x in h.Cultures.Except(b.Cultures, StringComparer.OrdinalIgnoreCase))
            c.Add("culture.added", new ObjectRef(ObjectKeys.Culture(x), "culture", x), after: x);
        foreach (var x in b.Cultures.Except(h.Cultures, StringComparer.OrdinalIgnoreCase))
            c.Add("culture.removed", new ObjectRef(ObjectKeys.Culture(x), "culture", x), before: x, onBase: true);
    }

    // ---------------- Tables ----------------

    private static void CompareTables(Collector c, ModelMatch match, ModelSnapshot b, ModelSnapshot h)
    {
        var movedMeasures = match.Measures.Pairs.Select(p => p.Head.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var t in match.Tables.Added)
        {
            var newMeasures = t.Measures.Count(m => !movedMeasures.Contains(m.Key));
            c.Add("table.added", TableRef(t), details: new ChangeDetails { Note = Contents(t.Columns.Count, newMeasures, t.Kind) });
        }
        var matchedBaseMeasures = match.Measures.Pairs.Select(p => p.Base.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var t in match.Tables.Removed)
        {
            var lostMeasures = t.Measures.Count(m => !matchedBaseMeasures.Contains(m.Key));
            c.Add("table.removed", TableRef(t), onBase: true, details: new ChangeDetails { Note = Contents(t.Columns.Count, lostMeasures, t.Kind) + " removed with it" });
        }

        foreach (var pair in match.Tables.Pairs)
        {
            var (bt, ht) = (pair.Base, pair.Head);
            var obj = TableRef(ht);
            if (pair.Rename is not null) c.Add("table.renamed", obj, before: bt.Name, after: ht.Name, rename: pair.Rename, baseKey: bt.Key);
            if (bt.Kind != ht.Kind) c.Add("table.kindChanged", obj, before: KindName(bt.Kind), after: KindName(ht.Kind));
            else if (ht.Kind == TableKind.CalculatedTable) c.Expr("table.expressionChanged", "table.expressionFormatting", obj, bt.Expression, ht.Expression, m: false, baseKey: bt.Key);
            c.Hidden("table", obj, bt.IsHidden, ht.IsHidden);
            c.Prop("table.descriptionChanged", obj, bt.Description, ht.Description);
            c.Prop("table.dataCategoryChanged", obj, bt.DataCategory, ht.DataCategory);
            if (bt.CalculationGroupPrecedence is { } bp && ht.CalculationGroupPrecedence is { } hp)
                c.Prop("calculationGroup.precedenceChanged", obj, bp.ToString(System.Globalization.CultureInfo.InvariantCulture), hp.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private static string Contents(int columns, int measures, TableKind kind)
    {
        var parts = new List<string>();
        if (kind != TableKind.Table) parts.Add(KindName(kind));
        parts.Add($"{columns} column{(columns == 1 ? "" : "s")}");
        parts.Add($"{measures} measure{(measures == 1 ? "" : "s")}");
        return string.Join(", ", parts);
    }

    private static string KindName(TableKind kind) => kind switch
    {
        TableKind.CalculatedTable => "calculated table",
        TableKind.CalculationGroup => "calculation group",
        _ => "table",
    };

    // ---------------- Measures (matched model-wide) ----------------

    private static void CompareMeasures(Collector c, ModelMatch match)
    {
        var addedTables = match.Tables.Added.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removedTables = match.Tables.Removed.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tableMap = match.Tables.Pairs.ToDictionary(p => p.Base.Name, p => p.Head.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var m in match.Measures.Added.Where(m => !addedTables.Contains(m.Table)))
            c.Add("measure.added", MeasureRef(m), after: m.Expression);
        foreach (var m in match.Measures.Removed.Where(m => !removedTables.Contains(m.Table)))
            c.Add("measure.removed", MeasureRef(m), before: m.Expression, onBase: true);

        foreach (var pair in match.Measures.Pairs)
        {
            var (bm, hm) = (pair.Base, pair.Head);
            var obj = MeasureRef(hm);
            if (pair.Rename is not null) c.Add("measure.renamed", obj, before: bm.Name, after: hm.Name, rename: pair.Rename, baseKey: bm.Key);
            var expectedTable = tableMap.GetValueOrDefault(bm.Table, bm.Table);
            if (!string.Equals(expectedTable, hm.Table, StringComparison.OrdinalIgnoreCase))
                c.Add("measure.moved", obj, before: bm.Table, after: hm.Table, baseKey: bm.Key);

            c.Expr("measure.expressionChanged", "measure.expressionFormatting", obj, bm.Expression, hm.Expression, m: false, baseKey: bm.Key);
            c.ExprProp("measure.formatStringExpressionChanged", obj, bm.FormatStringExpression, hm.FormatStringExpression);
            c.ExprProp("measure.detailRowsChanged", obj, bm.DetailRowsExpression, hm.DetailRowsExpression);
            c.Prop("measure.kpiChanged", obj, bm.HasKpi ? "KPI" : "none", hm.HasKpi ? "KPI" : "none");
            c.Hidden("measure", obj, bm.IsHidden, hm.IsHidden);
            c.Prop("measure.formatStringChanged", obj, bm.FormatString, hm.FormatString);
            c.Prop("measure.displayFolderChanged", obj, bm.DisplayFolder, hm.DisplayFolder);
            c.Prop("measure.descriptionChanged", obj, bm.Description, hm.Description);
        }
    }

    // ---------------- Columns, partitions, hierarchies, calculation items ----------------

    private static void CompareTableChildren(Collector c, TableChildren children, ModelSnapshot b, ModelSnapshot h)
    {
        var (bt, ht) = (children.Table.Base, children.Table.Head);

        foreach (var col in children.Columns.Added) c.Add("column.added", ColumnRef(col), after: col.Expression);
        foreach (var col in children.Columns.Removed) c.Add("column.removed", ColumnRef(col), before: col.Expression, onBase: true);
        foreach (var pair in children.Columns.Pairs)
        {
            var (bc, hc) = (pair.Base, pair.Head);
            var obj = ColumnRef(hc);
            if (pair.Rename is not null) c.Add("column.renamed", obj, before: bc.Name, after: hc.Name, rename: pair.Rename, baseKey: bc.Key);
            if (bc.Kind != hc.Kind)
                c.Add("column.kindChanged", obj, before: ColumnKindName(bc.Kind), after: ColumnKindName(hc.Kind));
            else if (hc.Kind == ColumnKind.Calculated)
                c.Expr("column.expressionChanged", "column.expressionFormatting", obj, bc.Expression, hc.Expression, m: false, baseKey: bc.Key);

            if (!string.Equals(bc.DataType, hc.DataType, StringComparison.Ordinal))
            {
                var isKey = IsRelationshipColumn(b, bt.Name, bc.Name) || IsRelationshipColumn(h, ht.Name, hc.Name);
                c.Add(isKey ? "column.keyDataTypeChanged" : "column.dataTypeChanged", obj, before: bc.DataType, after: hc.DataType,
                    details: isKey ? new ChangeDetails { Note = "used by a relationship" } : null);
            }
            if (hc.Kind == ColumnKind.Data) c.Prop("column.sourceColumnChanged", obj, bc.SourceColumn, hc.SourceColumn);
            c.Prop("column.summarizeByChanged", obj, bc.SummarizeBy, hc.SummarizeBy);
            c.Prop("column.sortByChanged", obj, bc.SortByColumn, hc.SortByColumn, mapped: children.Columns.Pairs.ToDictionary(p => p.Base.Name, p => p.Head.Name, StringComparer.OrdinalIgnoreCase));
            c.Prop("column.isKeyChanged", obj, bc.IsKey ? "true" : "false", hc.IsKey ? "true" : "false");
            c.Hidden("column", obj, bc.IsHidden, hc.IsHidden);
            c.Prop("column.formatStringChanged", obj, bc.FormatString, hc.FormatString);
            c.Prop("column.displayFolderChanged", obj, bc.DisplayFolder, hc.DisplayFolder);
            c.Prop("column.descriptionChanged", obj, bc.Description, hc.Description);
            c.Prop("column.dataCategoryChanged", obj, bc.DataCategory, hc.DataCategory);
        }

        // Calculated tables and calculation groups have one generated partition; their changes are reported on the table.
        if (ht.Kind == TableKind.Table && bt.Kind == TableKind.Table)
        {
            foreach (var p in children.Partitions.Added) c.Add("partition.added", PartitionRef(p), after: p.Expression);
            foreach (var p in children.Partitions.Removed) c.Add("partition.removed", PartitionRef(p), before: p.Expression, onBase: true);
            foreach (var pair in children.Partitions.Pairs)
            {
                var (bp, hp) = (pair.Base, pair.Head);
                var obj = PartitionRef(hp);
                if (bp.SourceType != hp.SourceType)
                    c.Add("partition.sourceTypeChanged", obj, before: bp.SourceType, after: hp.SourceType,
                        details: new ChangeDetails { ExpressionDiff = TextDiff.Unified(bp.Expression, hp.Expression) });
                else
                    c.Expr("partition.sourceChanged", "partition.sourceFormatting", obj, bp.Expression, hp.Expression, m: hp.SourceType is "m" or "query", baseKey: bp.Key);
                c.Prop("partition.modeChanged", obj, bp.Mode, hp.Mode);
            }
        }

        foreach (var x in children.Hierarchies.Added) c.Add("hierarchy.added", HierarchyRef(x));
        foreach (var x in children.Hierarchies.Removed) c.Add("hierarchy.removed", HierarchyRef(x), onBase: true);
        var columnMap = children.Columns.Pairs.ToDictionary(p => p.Base.Name, p => p.Head.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in children.Hierarchies.Pairs)
        {
            var (bh, hh) = (pair.Base, pair.Head);
            var obj = HierarchyRef(hh);
            if (pair.Rename is not null) c.Add("hierarchy.renamed", obj, before: bh.Name, after: hh.Name, rename: pair.Rename, baseKey: bh.Key);
            var before = string.Join(" > ", bh.Levels.Select(l => $"{l.Name} ({columnMap.GetValueOrDefault(l.Column, l.Column)})"));
            var after = string.Join(" > ", hh.Levels.Select(l => $"{l.Name} ({l.Column})"));
            if (!string.Equals(before, after, StringComparison.OrdinalIgnoreCase))
                c.Add("hierarchy.levelsChanged", obj, before: string.Join(" > ", bh.Levels.Select(l => $"{l.Name} ({l.Column})")), after: after);
            c.Hidden("hierarchy", obj, bh.IsHidden, hh.IsHidden);
            c.Prop("hierarchy.displayFolderChanged", obj, bh.DisplayFolder, hh.DisplayFolder);
            c.Prop("hierarchy.descriptionChanged", obj, bh.Description, hh.Description);
        }

        foreach (var x in children.CalculationItems.Added) c.Add("calculationItem.added", CalcItemRef(x), after: x.Expression);
        foreach (var x in children.CalculationItems.Removed) c.Add("calculationItem.removed", CalcItemRef(x), before: x.Expression, onBase: true);
        foreach (var pair in children.CalculationItems.Pairs)
        {
            var (bi, hi) = (pair.Base, pair.Head);
            var obj = CalcItemRef(hi);
            if (pair.Rename is not null) c.Add("calculationItem.renamed", obj, before: bi.Name, after: hi.Name, rename: pair.Rename, baseKey: bi.Key);
            c.Expr("calculationItem.expressionChanged", "calculationItem.expressionFormatting", obj, bi.Expression, hi.Expression, m: false, baseKey: bi.Key);
            c.Prop("calculationItem.ordinalChanged", obj, bi.Ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture), hi.Ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture));
            c.ExprProp("calculationItem.formatStringExpressionChanged", obj, bi.FormatStringExpression, hi.FormatStringExpression);
            c.Prop("calculationItem.descriptionChanged", obj, bi.Description, hi.Description);
        }
    }

    private static bool IsRelationshipColumn(ModelSnapshot model, string table, string column) =>
        model.Relationships.Any(r =>
            string.Equals(r.FromTable, table, StringComparison.OrdinalIgnoreCase) && string.Equals(r.FromColumn, column, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(r.ToTable, table, StringComparison.OrdinalIgnoreCase) && string.Equals(r.ToColumn, column, StringComparison.OrdinalIgnoreCase));

    private static string ColumnKindName(ColumnKind kind) => kind switch
    {
        ColumnKind.Calculated => "calculated",
        ColumnKind.CalculatedTableColumn => "calculated table column",
        _ => "data",
    };

    // ---------------- Relationships ----------------

    private static void CompareRelationships(Collector c, ModelMatch match)
    {
        foreach (var r in match.Relationships.Added) c.Add("relationship.added", RelationshipRef(r), after: Describe(r));
        foreach (var r in match.Relationships.Removed) c.Add("relationship.removed", RelationshipRef(r), before: Describe(r), onBase: true);
        foreach (var pair in match.Relationships.Pairs)
        {
            var (br, hr) = (pair.Base, pair.Head);
            var obj = RelationshipRef(hr);
            c.Prop("relationship.crossFilterChanged", obj, br.CrossFilteringBehavior, hr.CrossFilteringBehavior);
            c.Prop("relationship.cardinalityChanged", obj, $"{br.FromCardinality}-to-{br.ToCardinality}", $"{hr.FromCardinality}-to-{hr.ToCardinality}");
            c.Prop("relationship.activeChanged", obj, br.IsActive ? "active" : "inactive", hr.IsActive ? "active" : "inactive");
            c.Prop("relationship.securityFilteringChanged", obj, br.SecurityFilteringBehavior, hr.SecurityFilteringBehavior);
            c.Prop("relationship.referentialIntegrityChanged", obj, br.RelyOnReferentialIntegrity ? "true" : "false", hr.RelyOnReferentialIntegrity ? "true" : "false");
        }
    }

    private static string Describe(RelationshipSnapshot r) =>
        $"{r.FromCardinality}-to-{r.ToCardinality}, {r.CrossFilteringBehavior}{(r.IsActive ? "" : ", inactive")}";

    // ---------------- Roles ----------------

    private static void CompareRoles(Collector c, ModelMatch match)
    {
        var tableMap = match.Tables.Pairs.ToDictionary(p => p.Base.Name, p => p.Head.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var r in match.Roles.Added) c.Add("role.added", RoleRef(r), after: r.ModelPermission);
        foreach (var r in match.Roles.Removed) c.Add("role.removed", RoleRef(r), before: r.ModelPermission, onBase: true);
        foreach (var pair in match.Roles.Pairs)
        {
            var (br, hr) = (pair.Base, pair.Head);
            var obj = RoleRef(hr);
            c.Prop("role.modelPermissionChanged", obj, br.ModelPermission, hr.ModelPermission);
            c.Prop("role.membersChanged", obj, br.MemberCount.ToString(System.Globalization.CultureInfo.InvariantCulture), hr.MemberCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
            c.Prop("role.descriptionChanged", obj, br.Description, hr.Description);

            var basePerms = br.TablePermissions.ToDictionary(tp => tableMap.GetValueOrDefault(tp.Table, tp.Table), StringComparer.OrdinalIgnoreCase);
            var headPerms = hr.TablePermissions.ToDictionary(tp => tp.Table, StringComparer.OrdinalIgnoreCase);
            foreach (var table in basePerms.Keys.Union(headPerms.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
            {
                var bp = basePerms.GetValueOrDefault(table);
                var hp = headPerms.GetValueOrDefault(table);
                var permObj = new ObjectRef(ObjectKeys.TablePermission(hr.Name, hp?.Table ?? table), "tablePermission", hp?.Table ?? table, null);
                var baseKey = bp is null ? null : ObjectKeys.TablePermission(br.Name, bp.Table);
                var bf = bp?.FilterExpression;
                var hf = hp?.FilterExpression;
                if (string.IsNullOrWhiteSpace(bf) && !string.IsNullOrWhiteSpace(hf)) c.Add("role.rlsFilterAdded", permObj, after: hf);
                else if (!string.IsNullOrWhiteSpace(bf) && string.IsNullOrWhiteSpace(hf)) c.Add("role.rlsFilterRemoved", permObj, before: bf, baseKey: baseKey, onBase: hp is null);
                else if (!string.IsNullOrWhiteSpace(bf)) c.Expr("role.rlsFilterChanged", "role.rlsFilterFormatting", permObj, bf, hf, m: false, baseKey: baseKey);

                var bOls = bp is null ? "" : Ols(bp);
                var hOls = hp is null ? "" : Ols(hp);
                if (!string.Equals(bOls, hOls, StringComparison.OrdinalIgnoreCase))
                    c.Add("role.olsChanged", permObj, before: bOls.Length == 0 ? "(none)" : bOls, after: hOls.Length == 0 ? "(none)" : hOls, baseKey: baseKey);
            }
        }
    }

    /// <summary>Object-level security of one table permission as text; empty when it grants default access.</summary>
    private static string Ols(TablePermissionSnapshot tp)
    {
        var parts = new List<string>();
        if (tp.MetadataPermission is not ("default" or "read")) parts.Add($"table: {tp.MetadataPermission}");
        parts.AddRange(tp.ColumnPermissions.Where(cp => cp.MetadataPermission is not ("default" or "read"))
            .OrderBy(cp => cp.Column, StringComparer.OrdinalIgnoreCase).Select(cp => $"[{cp.Column}]: {cp.MetadataPermission}"));
        return string.Join(", ", parts);
    }

    // ---------------- Expressions and functions ----------------

    private static void CompareExpressions(Collector c, ModelMatch match)
    {
        foreach (var e in match.Expressions.Added) c.Add("expression.added", ExpressionRef(e), after: e.Expression);
        foreach (var e in match.Expressions.Removed) c.Add("expression.removed", ExpressionRef(e), before: e.Expression, onBase: true);
        foreach (var pair in match.Expressions.Pairs)
        {
            var (be, he) = (pair.Base, pair.Head);
            var obj = ExpressionRef(he);
            if (pair.Rename is not null) c.Add("expression.renamed", obj, before: be.Name, after: he.Name, rename: pair.Rename, baseKey: be.Key);
            if (be.Kind != he.Kind) c.Add("expression.kindChanged", obj, before: be.Kind, after: he.Kind);
            else c.Expr("expression.expressionChanged", "expression.expressionFormatting", obj, be.Expression, he.Expression, m: he.Kind == "m", baseKey: be.Key);
            c.Prop("expression.descriptionChanged", obj, be.Description, he.Description);
        }
    }

    private static void CompareFunctions(Collector c, ModelMatch match)
    {
        foreach (var f in match.Functions.Added) c.Add("function.added", FunctionRef(f), after: f.Expression);
        foreach (var f in match.Functions.Removed) c.Add("function.removed", FunctionRef(f), before: f.Expression, onBase: true);
        foreach (var pair in match.Functions.Pairs)
        {
            var (bf, hf) = (pair.Base, pair.Head);
            var obj = FunctionRef(hf);
            if (pair.Rename is not null) c.Add("function.renamed", obj, before: bf.Name, after: hf.Name, rename: pair.Rename, baseKey: bf.Key);
            c.Expr("function.expressionChanged", "function.expressionFormatting", obj, bf.Expression, hf.Expression, m: false, baseKey: bf.Key);
            c.Hidden("function", obj, bf.IsHidden, hf.IsHidden);
            c.Prop("function.descriptionChanged", obj, bf.Description, hf.Description);
        }
    }

    // ---------------- Object refs ----------------

    private static ObjectRef TableRef(TableSnapshot t) => new(t.Key, t.Kind == TableKind.CalculationGroup ? "calculationGroup" : "table", t.Name);
    private static ObjectRef MeasureRef(MeasureSnapshot m) => new(m.Key, "measure", m.Name, m.Table);
    private static ObjectRef ColumnRef(ColumnSnapshot c) => new(c.Key, "column", c.Name, c.Table);
    private static ObjectRef PartitionRef(PartitionSnapshot p) => new(p.Key, "partition", p.Name, p.Table);
    private static ObjectRef HierarchyRef(HierarchySnapshot h) => new(h.Key, "hierarchy", h.Name, h.Table);
    private static ObjectRef CalcItemRef(CalculationItemSnapshot c) => new(c.Key, "calculationItem", c.Name, c.Table);
    private static ObjectRef RelationshipRef(RelationshipSnapshot r) => new(r.Key, "relationship", r.Key["relationship:".Length..]);
    private static ObjectRef RoleRef(RoleSnapshot r) => new(r.Key, "role", r.Name);
    private static ObjectRef ExpressionRef(NamedExpressionSnapshot e) => new(e.Key, e.IsParameter ? "parameter" : "expression", e.Name);
    private static ObjectRef FunctionRef(FunctionSnapshot f) => new(f.Key, "function", f.Name);

    // ---------------- Collector ----------------

    private sealed class Collector(DiffInput @base, DiffInput head, DiffOptions options)
    {
        public List<Change> Changes { get; } = [];
        public int FormattingHidden { get; private set; }

        /// <param name="onBase">The object only exists in base (removed): location and impact come from the base model.</param>
        /// <param name="baseKey">Key of the object in base, when it differs from the head key (renames).</param>
        public void Add(string kind, ObjectRef obj, string? before = null, string? after = null, RenameInfo? rename = null,
            ChangeDetails? details = null, bool onBase = false, string? baseKey = null)
        {
            var severity = SeverityPolicy.Resolve(kind, options.SeverityOverrides);
            var side = onBase ? @base : head;
            var location = side.Locations?.Find(obj.Key) ?? (baseKey is null ? null : @base.Locations?.Find(baseKey));
            var isAdd = kind.EndsWith(".added", StringComparison.Ordinal);
            var impact = isAdd ? null : Impact(side.Dependencies, obj.Key) ?? (onBase || baseKey is null ? null : Impact(@base.Dependencies, baseKey));

            Changes.Add(new Change
            {
                Id = $"{kind}:{obj.Key}",
                Kind = kind,
                Severity = severity,
                Object = obj,
                Rename = rename,
                Before = before,
                After = after,
                Details = details,
                Impact = impact,
                Location = location,
            });
        }

        /// <summary>Adds a change when a scalar property differs. <paramref name="mapped"/> translates base values (e.g. renamed sort-by columns).</summary>
        public void Prop(string kind, ObjectRef obj, string? before, string? after, IReadOnlyDictionary<string, string>? mapped = null)
        {
            var comparable = before is not null && mapped is not null && mapped.TryGetValue(before, out var m) ? m : before;
            if (string.Equals(comparable ?? "", after ?? "", StringComparison.Ordinal)) return;
            Add(kind, obj, before ?? "(none)", after ?? "(none)");
        }

        public void Hidden(string type, ObjectRef obj, bool before, bool after)
        {
            if (before == after) return;
            Add(after ? $"{type}.hidden" : $"{type}.unhidden", obj, before ? "hidden" : "visible", after ? "hidden" : "visible");
        }

        /// <summary>An expression that is a secondary property: changed only if its significant tokens differ.</summary>
        public void ExprProp(string kind, ObjectRef obj, string? before, string? after)
        {
            if (ObjectMatcher.Normalize(before, m: false) == ObjectMatcher.Normalize(after, m: false)) return;
            Add(kind, obj, before ?? "(none)", after ?? "(none)", details: new ChangeDetails { FormattingOnly = false, ExpressionDiff = TextDiff.Unified(before, after) });
        }

        /// <summary>A main expression: a real change, a formatting-only change (hidden by default), or nothing.</summary>
        public void Expr(string changedKind, string formattingKind, ObjectRef obj, string? before, string? after, bool m, string? baseKey)
        {
            if (string.Equals(before ?? "", after ?? "", StringComparison.Ordinal)) return;
            var formattingOnly = ObjectMatcher.Normalize(before, m) == ObjectMatcher.Normalize(after, m);
            if (formattingOnly && !options.IncludeFormattingOnly)
            {
                FormattingHidden++;
                return;
            }
            Add(formattingOnly ? formattingKind : changedKind, obj, before, after, baseKey: baseKey,
                details: new ChangeDetails { FormattingOnly = formattingOnly, ExpressionDiff = TextDiff.Unified(before, after) });
        }

        /// <summary>Everything that uses the object through DAX or structurally, transitively.</summary>
        private static ChangeImpact? Impact(DependencyGraph? graph, string key)
        {
            if (graph is null || !graph.Contains(key)) return null;
            var seen = new HashSet<string>(StringComparer.Ordinal) { key };
            var queue = new Queue<string>([key]);
            var found = new List<string>();
            while (queue.Count > 0 && found.Count < 5000)
            {
                var current = queue.Dequeue();
                foreach (var next in graph.ReferencesTo(current).Select(r => r.Owner).Concat(graph.StructuralUsesOf(current).Select(s => s.User)))
                {
                    if (!seen.Add(next)) continue;
                    found.Add(next);
                    queue.Enqueue(next);
                }
            }
            if (found.Count == 0) return null;
            return new ChangeImpact(found.Take(MaxImpactListed).ToList(), found.Count);
        }
    }
}
