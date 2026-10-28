using TabularTools.Core.Config;
using TabularTools.Core.Dax;
using TabularTools.Core.Queries;
using TabularTools.Core.Snapshots;

namespace TabularTools.Core.Testing;

/// <summary>A test case checked against the head model: measures and columns found, filters and tolerance merged.</summary>
public sealed record ResolvedCase
{
    public required TestCase Case { get; init; }
    public string Id => Case.Id!;
    /// <summary>Head measures, in the order the case lists them.</summary>
    public required IReadOnlyList<MeasureSnapshot> Measures { get; init; }
    public required IReadOnlyList<ColumnSnapshot> GroupBy { get; init; }
    /// <summary>Default filters, then the case's own.</summary>
    public required IReadOnlyList<string> Filters { get; init; }
    public required Tolerance Tolerance { get; init; }
    public required IReadOnlyList<(AssertionKind Kind, TestAssertion Assertion)> Assertions { get; init; }
    /// <summary>Why the case can't run against this model, if it can't.</summary>
    public IReadOnlyList<string> Problems { get; init; } = [];
    public required ModelSnapshot Model { get; init; }

    /// <summary>Keys of the head measures and columns a DAX fragment (filter, invariant) names.</summary>
    public IEnumerable<string> HeadReferences(string dax)
    {
        var tokens = DaxLexer.Significant(dax);
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Type != DaxTokenType.COLUMN_OR_MEASURE) continue;
            var name = tokens[i].Value;
            var qualifier = i > 0 && tokens[i - 1].Type is DaxTokenType.TABLE or DaxTokenType.TABLE_OR_VARIABLE ? tokens[i - 1].Value : null;
            var measure = Model.AllMeasures.FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                && (qualifier is null || m.Table.Equals(qualifier, StringComparison.OrdinalIgnoreCase)));
            if (measure is not null) { yield return measure.Key; continue; }
            if (qualifier is not null && Model.AllColumns.FirstOrDefault(c => c.Table.Equals(qualifier, StringComparison.OrdinalIgnoreCase)
                    && c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } column)
                yield return column.Key;
        }
    }
}

public static class CaseResolver
{
    public static ResolvedCase Resolve(TestCase testCase, MeasureTestFile file, ModelSnapshot model, DependencyGraph graph, ToleranceSettings? configTolerance)
    {
        ArgumentNullException.ThrowIfNull(testCase);
        ArgumentNullException.ThrowIfNull(file);
        var problems = new List<string>();
        var measures = new List<MeasureSnapshot>();
        foreach (var spec in testCase.Measures ?? [])
        {
            var key = TryResolve(model, graph, spec, problems);
            if (key is null) continue;
            var measure = model.AllMeasures.FirstOrDefault(m => m.Key == key);
            if (measure is null) problems.Add($"'{spec}' is a {Describe(graph, key)}, not a measure. List columns under groupBy.");
            else if (measures.Any(m => m.Key == measure.Key)) problems.Add($"measure '{spec}' is listed twice.");
            else measures.Add(measure);
        }

        var columns = new List<ColumnSnapshot>();
        foreach (var spec in testCase.GroupBy ?? [])
        {
            var key = TryResolve(model, graph, spec, problems);
            if (key is null) continue;
            var column = model.AllColumns.FirstOrDefault(c => c.Key == key);
            if (column is null) problems.Add($"groupBy '{spec}' is a {Describe(graph, key)}, not a column.");
            else if (columns.Any(c => c.Key == column.Key)) problems.Add($"groupBy column '{spec}' is listed twice.");
            else columns.Add(column);
        }

        var filters = (file.Defaults?.Filters ?? []).Concat(testCase.Filters ?? []).Select(f => f.Trim()).ToList();
        foreach (var f in filters) CheckReferences(model, f, $"filter '{f}'", problems);

        var assertions = (testCase.Assertions is { Count: > 0 } list ? list : [new TestAssertion { Type = "differential" }])
            .Select(a => (AssertionKinds.Parse(a.Type, testCase.Id!), a)).ToList();
        foreach (var (kind, a) in assertions)
        {
            if (kind == AssertionKind.Invariant) CheckReferences(model, a.Expr!, $"invariant '{a.Expr}'", problems);
            if (kind == AssertionKind.Snapshot) CheckSnapshotColumns(a, measures, columns, problems);
        }

        if (testCase.AsRole is { } role && !model.Roles.Any(r => r.Name.Equals(role, StringComparison.OrdinalIgnoreCase)))
            problems.Add($"role '{role}' isn't in the model.");

        return new ResolvedCase
        {
            Case = testCase,
            Measures = measures,
            GroupBy = columns,
            Filters = filters,
            Tolerance = MergeTolerance(testCase.Tolerance, file.Defaults?.Tolerance, configTolerance),
            Assertions = assertions,
            Problems = problems,
            Model = model,
        };
    }

    public static Tolerance MergeTolerance(params ToleranceSettings?[] layers)
    {
        double? absolute = null, relative = null;
        foreach (var t in layers)
        {
            absolute ??= t?.Absolute;
            relative ??= t?.Relative;
        }
        return new Tolerance(absolute ?? Tolerance.Default.Absolute, relative ?? Tolerance.Default.Relative);
    }

    private static string? TryResolve(ModelSnapshot model, DependencyGraph graph, string spec, List<string> problems)
    {
        try { return DependencyQuery.ResolveObject(model, graph, spec); }
        catch (ArgumentException ex) { problems.Add($"'{spec}': {ex.Message}"); return null; }
    }

    private static string Describe(DependencyGraph graph, string key) => graph.ObjectTypes.TryGetValue(key, out var t) ? t : "object";

    /// <summary>Checks that <c>Table[Name]</c> and <c>[Name]</c> references in a DAX fragment exist, to catch typos before querying.</summary>
    private static void CheckReferences(ModelSnapshot model, string dax, string what, List<string> problems)
    {
        var tokens = DaxLexer.Significant(dax);
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Type != DaxTokenType.COLUMN_OR_MEASURE) continue;
            var qualifier = i > 0 && tokens[i - 1].Type is DaxTokenType.TABLE or DaxTokenType.TABLE_OR_VARIABLE ? tokens[i - 1].Value : null;
            if (qualifier is not null)
            {
                var table = model.Tables.FirstOrDefault(x => x.Name.Equals(qualifier, StringComparison.OrdinalIgnoreCase));
                // Unknown qualifiers may be variables or query tables; only flag known tables with unknown members.
                if (table is not null && !table.Columns.Any(c => Eq(c.Name, t.Value)) && !table.Measures.Any(m => Eq(m.Name, t.Value)))
                    problems.Add($"{what}: table '{table.Name}' has no column or measure [{t.Value}].");
            }
            else if (!model.AllMeasures.Any(m => Eq(m.Name, t.Value)) && !model.AllColumns.Any(c => Eq(c.Name, t.Value)))
            {
                problems.Add($"{what}: no measure or column named [{t.Value}].");
            }
        }
    }

    private static void CheckSnapshotColumns(TestAssertion a, IReadOnlyList<MeasureSnapshot> measures, IReadOnlyList<ColumnSnapshot> groupBy, List<string> problems)
    {
        foreach (var spec in a.Columns!)
            if (MatchSnapshotColumn(spec, measures, groupBy) is null)
                problems.Add($"snapshot column '{spec}' isn't one of the case's measures or groupBy columns.");
        foreach (var g in groupBy)
            if (!a.Columns!.Any(spec => MatchSnapshotColumn(spec, measures, groupBy) == g.Key))
                problems.Add($"snapshot columns must include groupBy column '{g.Table}'[{g.Name}] (rows are matched on it).");
    }

    /// <summary>Key of the case measure or group-by column a snapshot column names, or null.</summary>
    public static string? MatchSnapshotColumn(string spec, IReadOnlyList<MeasureSnapshot> measures, IReadOnlyList<ColumnSnapshot> groupBy)
    {
        var tokens = DaxLexer.Significant(spec);
        if (tokens.Count == 1 && tokens[0].Type == DaxTokenType.COLUMN_OR_MEASURE)
        {
            var name = tokens[0].Value;
            return measures.FirstOrDefault(m => Eq(m.Name, name))?.Key
                   ?? (groupBy.Count(c => Eq(c.Name, name)) == 1 ? groupBy.First(c => Eq(c.Name, name)).Key : null);
        }
        if (tokens.Count == 2 && tokens[0].Type is DaxTokenType.TABLE or DaxTokenType.TABLE_OR_VARIABLE && tokens[1].Type == DaxTokenType.COLUMN_OR_MEASURE)
        {
            var (table, name) = (tokens[0].Value, tokens[1].Value);
            return groupBy.FirstOrDefault(c => Eq(c.Table, table) && Eq(c.Name, name))?.Key
                   ?? measures.FirstOrDefault(m => Eq(m.Table, table) && Eq(m.Name, name))?.Key;
        }
        return null;
    }

    private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
