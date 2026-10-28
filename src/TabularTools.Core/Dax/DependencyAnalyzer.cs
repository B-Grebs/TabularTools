using TabularTools.Core.Snapshots;

namespace TabularTools.Core.Dax;

/// <summary>
/// Resolves references in every DAX expression of a model: measures (expression, format string, detail rows),
/// calculated columns and tables, calculation items, RLS filters and user-defined functions.
/// </summary>
public static class DependencyAnalyzer
{
    public static DependencyGraph Analyze(ModelSnapshot model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var index = new NameIndex(model);
        var references = new List<DaxReference>();

        foreach (var table in model.Tables)
        {
            if (table.Expression is { } calcTable)
                references.AddRange(Resolve(index, table.Key, ExpressionPart.Expression, calcTable, table.Name));

            foreach (var column in table.Columns.Where(c => c.Expression is not null))
                references.AddRange(Resolve(index, column.Key, ExpressionPart.Expression, column.Expression!, table.Name));

            foreach (var measure in table.Measures)
            {
                references.AddRange(Resolve(index, measure.Key, ExpressionPart.Expression, measure.Expression, null));
                if (measure.FormatStringExpression is { } fs)
                    references.AddRange(Resolve(index, measure.Key, ExpressionPart.FormatString, fs, null));
                if (measure.DetailRowsExpression is { } dr)
                    references.AddRange(Resolve(index, measure.Key, ExpressionPart.DetailRows, dr, null));
            }

            foreach (var item in table.CalculationItems)
            {
                references.AddRange(Resolve(index, item.Key, ExpressionPart.Expression, item.Expression, null));
                if (item.FormatStringExpression is { } fs)
                    references.AddRange(Resolve(index, item.Key, ExpressionPart.FormatString, fs, null));
            }
        }

        foreach (var role in model.Roles)
            foreach (var permission in role.TablePermissions.Where(p => !string.IsNullOrWhiteSpace(p.FilterExpression)))
                references.AddRange(Resolve(index, ObjectKeys.TablePermission(role.Name, permission.Table), ExpressionPart.Filter,
                    permission.FilterExpression!, permission.Table));

        foreach (var function in model.Functions)
            references.AddRange(Resolve(index, function.Key, ExpressionPart.Expression, function.Expression, null, isFunction: true));

        var structural = new List<StructuralUse>();
        foreach (var r in model.Relationships)
        {
            structural.Add(new StructuralUse(StructuralUseKind.Relationship, r.Key, ObjectKeys.Column(r.FromTable, r.FromColumn)));
            structural.Add(new StructuralUse(StructuralUseKind.Relationship, r.Key, ObjectKeys.Column(r.ToTable, r.ToColumn)));
        }
        foreach (var c in model.AllColumns.Where(c => c.SortByColumn is not null))
            structural.Add(new StructuralUse(StructuralUseKind.SortBy, c.Key, ObjectKeys.Column(c.Table, c.SortByColumn!)));
        foreach (var h in model.AllHierarchies)
            foreach (var level in h.Levels)
                structural.Add(new StructuralUse(StructuralUseKind.Hierarchy, h.Key, ObjectKeys.Column(h.Table, level.Column)));

        return new DependencyGraph(references, structural, index.ObjectTypes);
    }

    private static IEnumerable<DaxReference> Resolve(NameIndex index, string owner, ExpressionPart part, string expression, string? contextTable, bool isFunction = false)
    {
        var tokens = DaxLexer.Significant(expression);
        var variables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var literalNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tablesInExpression = new List<string>();

        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Type == DaxTokenType.VAR && i + 1 < tokens.Count) variables.Add(tokens[i + 1].Value);
            if (t.Type == DaxTokenType.STRING_LITERAL) literalNames.Add(t.Value);
            if (t.Type is DaxTokenType.TABLE or DaxTokenType.TABLE_OR_VARIABLE && index.Table(t.Value) is { } tableName)
                tablesInExpression.Add(tableName);
        }

        // UDF parameters: identifiers before the first "=>".
        if (isFunction)
        {
            var arrow = tokens.ToList().FindIndex(t => t.Type == DaxTokenType.ARROW);
            for (var i = 0; i < arrow; i++)
                if (tokens[i].Type == DaxTokenType.TABLE_OR_VARIABLE) variables.Add(tokens[i].Value);
        }

        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            var next = i + 1 < tokens.Count ? tokens[i + 1] : null;

            // Table[Name] — qualified column or measure.
            if (t.Type is DaxTokenType.TABLE or DaxTokenType.TABLE_OR_VARIABLE && next?.Type == DaxTokenType.COLUMN_OR_MEASURE)
            {
                i++;
                if (t.Type == DaxTokenType.TABLE_OR_VARIABLE && variables.Contains(t.Value)) continue; // column of a table variable
                var text = t.Text + next.Text;
                var table = index.Table(t.Value);
                if (table is null)
                {
                    yield return new DaxReference(owner, part, ReferenceKind.Column, t.Value, next.Value, true, null, text, t.Line, t.Column);
                    continue;
                }
                if (index.Column(table, next.Value) is { } column)
                    yield return new DaxReference(owner, part, ReferenceKind.Column, table, column, true, ObjectKeys.Column(table, column), text, t.Line, t.Column);
                else if (index.Measure(next.Value) is { } m && string.Equals(m.Table, table, StringComparison.OrdinalIgnoreCase))
                    yield return new DaxReference(owner, part, ReferenceKind.Measure, m.Table, m.Name, true, ObjectKeys.Measure(m.Table, m.Name), text, t.Line, t.Column);
                else
                    yield return new DaxReference(owner, part, ReferenceKind.Column, table, next.Value, true, null, text, t.Line, t.Column);
                continue;
            }

            switch (t.Type)
            {
                case DaxTokenType.COLUMN_OR_MEASURE:
                {
                    if (index.Measure(t.Value) is { } m)
                    {
                        yield return new DaxReference(owner, part, ReferenceKind.Measure, m.Table, m.Name, false, ObjectKeys.Measure(m.Table, m.Name), t.Text, t.Line, t.Column);
                        break;
                    }
                    var home = ResolveUnqualifiedColumn(index, t.Value, contextTable, tablesInExpression);
                    if (home is not null)
                        yield return new DaxReference(owner, part, ReferenceKind.Column, home.Value.Table, home.Value.Column, false,
                            ObjectKeys.Column(home.Value.Table, home.Value.Column), t.Text, t.Line, t.Column);
                    else if (!literalNames.Contains(t.Value))
                        yield return new DaxReference(owner, part, ReferenceKind.Column, null, t.Value, false, null, t.Text, t.Line, t.Column);
                    break;
                }
                case DaxTokenType.TABLE:
                {
                    var table = index.Table(t.Value);
                    yield return new DaxReference(owner, part, ReferenceKind.Table, table ?? t.Value, table ?? t.Value, true,
                        table is null ? null : ObjectKeys.Table(table), t.Text, t.Line, t.Column);
                    break;
                }
                case DaxTokenType.TABLE_OR_VARIABLE when !variables.Contains(t.Value) && index.Table(t.Value) is { } table:
                    yield return new DaxReference(owner, part, ReferenceKind.Table, table, table, true, ObjectKeys.Table(table), t.Text, t.Line, t.Column);
                    break;
                case DaxTokenType.FUNCTION when index.Function(t.Value) is { } function:
                    yield return new DaxReference(owner, part, ReferenceKind.Function, null, function, true, ObjectKeys.Function(function), t.Text, t.Line, t.Column);
                    break;
            }
        }
    }

    /// <summary>
    /// An unqualified <c>[Name]</c> that isn't a measure: prefer the context table (calculated column, RLS filter),
    /// then tables named in the same expression (iterators), then a column name that is unique in the model.
    /// </summary>
    private static (string Table, string Column)? ResolveUnqualifiedColumn(NameIndex index, string name, string? contextTable, List<string> tablesInExpression)
    {
        if (contextTable is not null && index.Column(contextTable, name) is { } c1) return (contextTable, c1);
        foreach (var table in tablesInExpression)
            if (index.Column(table, name) is { } c2) return (table, c2);
        var owners = index.TablesWithColumn(name);
        return owners.Count == 1 ? (owners[0], index.Column(owners[0], name)!) : null;
    }

    /// <summary>Case-insensitive name lookups (DAX names are case-insensitive).</summary>
    private sealed class NameIndex
    {
        private readonly Dictionary<string, string> _tables = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Dictionary<string, string>> _columns = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (string Table, string Name)> _measures = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<string>> _columnOwners = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _functions = new(StringComparer.OrdinalIgnoreCase);

        public NameIndex(ModelSnapshot model)
        {
            var types = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var t in model.Tables)
            {
                _tables[t.Name] = t.Name;
                types[t.Key] = "table";
                var columns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var c in t.Columns)
                {
                    columns[c.Name] = c.Name;
                    types[c.Key] = "column";
                    if (!_columnOwners.TryGetValue(c.Name, out var owners)) _columnOwners[c.Name] = owners = [];
                    owners.Add(t.Name);
                }
                _columns[t.Name] = columns;
                foreach (var m in t.Measures)
                {
                    _measures.TryAdd(m.Name, (t.Name, m.Name));
                    types[m.Key] = "measure";
                }
                foreach (var ci in t.CalculationItems) types[ci.Key] = "calculationItem";
                foreach (var h in t.Hierarchies) types[h.Key] = "hierarchy";
            }
            foreach (var f in model.Functions)
            {
                _functions[f.Name] = f.Name;
                types[f.Key] = "function";
            }
            foreach (var r in model.Relationships) types[r.Key] = "relationship";
            foreach (var role in model.Roles)
            {
                types[role.Key] = "role";
                foreach (var tp in role.TablePermissions) types[ObjectKeys.TablePermission(role.Name, tp.Table)] = "tablePermission";
            }
            ObjectTypes = types;
        }

        public IReadOnlyDictionary<string, string> ObjectTypes { get; }

        public string? Table(string name) => _tables.TryGetValue(name, out var t) ? t : null;

        public string? Column(string table, string name) =>
            _columns.TryGetValue(table, out var cols) && cols.TryGetValue(name, out var c) ? c : null;

        public (string Table, string Name)? Measure(string name) => _measures.TryGetValue(name, out var m) ? m : null;

        public string? Function(string name) => _functions.TryGetValue(name, out var f) ? f : null;

        public IReadOnlyList<string> TablesWithColumn(string name) => _columnOwners.TryGetValue(name, out var list) ? list : [];
    }
}
