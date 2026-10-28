using TabularTools.Core.Queries;

namespace TabularTools.Core.Testing;

/// <summary>Compares query results row by row on the group-by key, never by position (ARCHITECTURE §11.3).</summary>
public static class ResultComparer
{
    /// <summary>A result indexed by group key: key text → (key values, value by column key).</summary>
    public sealed record IndexedResult(IReadOnlyList<string> Order, IReadOnlyDictionary<string, (IReadOnlyDictionary<string, object?> Key, IReadOnlyDictionary<string, object?> Values)> Rows);

    public static IndexedResult Index(PlannedQuery query, DaxResult result)
    {
        if (result.Columns.Count != query.Columns.Count)
            throw new DaxQueryException($"The result has {result.Columns.Count} columns; the query asked for {query.Columns.Count}. Is the recording for an older query?");
        var order = new List<string>();
        var rows = new Dictionary<string, (IReadOnlyDictionary<string, object?>, IReadOnlyDictionary<string, object?>)>(StringComparer.Ordinal);
        foreach (var row in result.Rows)
        {
            var key = new Dictionary<string, object?>(StringComparer.Ordinal);
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < query.Columns.Count; i++)
            {
                var c = query.Columns[i];
                if (c.IsGroup) key[c.Label] = row[i];
                else values[c.Key] = row[i];
            }
            var text = KeyText(key.Values);
            if (rows.ContainsKey(text)) throw new DaxQueryException($"The result has two rows for {Describe(key)}; group-by values must be unique.");
            order.Add(text);
            rows[text] = (key, values);
        }
        return new IndexedResult(order, rows);
    }

    public static string KeyText(IEnumerable<object?> values) => string.Join("\u001f", values.Select(v => (v?.GetType().Name ?? "null") + ":" + DaxValues.Format(v)));

    public static string Describe(IReadOnlyDictionary<string, object?> key) =>
        key.Count == 0 ? "the total" : string.Join(", ", key.Select(kv => $"{kv.Key} = {DaxValues.Format(kv.Value)}"));

    /// <summary>
    /// Differential comparison of base and head for each head measure of the case. Rows missing on one side count as
    /// BLANK there (SUMMARIZECOLUMNS drops rows where every measure is blank).
    /// </summary>
    public static (IReadOnlyList<MeasureOutcome> Measures, IReadOnlyList<CellDiff> Diffs) Differential(
        ResolvedCase c, IndexedResult @base, IndexedResult head, IReadOnlySet<string> added, IReadOnlySet<string> edited)
    {
        var keys = head.Order.Concat(@base.Order.Where(k => !head.Rows.ContainsKey(k))).ToList();
        var outcomes = new List<MeasureOutcome>();
        var diffs = new List<CellDiff>();
        foreach (var m in c.Measures)
        {
            if (added.Contains(m.Key)) { outcomes.Add(new MeasureOutcome(m.Name, MeasureChange.Added, 0)); continue; }
            var expected = edited.Contains(m.Key);
            var changed = 0;
            foreach (var k in keys)
            {
                var hasB = @base.Rows.TryGetValue(k, out var b);
                var hasH = head.Rows.TryGetValue(k, out var h);
                var bv = hasB ? b.Values.GetValueOrDefault(m.Key) : null;
                var hv = hasH ? h.Values.GetValueOrDefault(m.Key) : null;
                if (DaxValues.Equal(bv, hv, c.Tolerance)) continue;
                changed++;
                diffs.Add(new CellDiff(hasH ? h.Key : b.Key, m.Name, bv, hv, DaxValues.Delta(bv, hv), expected));
            }
            outcomes.Add(new MeasureOutcome(m.Name, changed == 0 ? MeasureChange.Unchanged : expected ? MeasureChange.ExpectedChange : MeasureChange.Regression, changed));
        }
        return (outcomes, diffs.OrderBy(d => d.Expected).ToList());
    }

    /// <summary>Compares a live result with a stored snapshot. Diffs carry the stored value as Base and the live one as Head.</summary>
    public static IReadOnlyList<CellDiff> Snapshot(ResolvedCase c, TestAssertion snapshot, IndexedResult live)
    {
        var specs = snapshot.Columns!.Select(spec => CaseResolver.MatchSnapshotColumn(spec, c.Measures, c.GroupBy)!).ToList();
        var groupLabels = c.GroupBy.ToDictionary(g => g.Key, g => $"{g.Table}[{g.Name}]", StringComparer.Ordinal);
        var measureNames = c.Measures.ToDictionary(m => m.Key, m => m.Name, StringComparer.Ordinal);

        var stored = new Dictionary<string, (IReadOnlyDictionary<string, object?> Key, Dictionary<string, object?> Values)>(StringComparer.Ordinal);
        var storedOrder = new List<string>();
        foreach (var row in snapshot.Rows!)
        {
            var key = new Dictionary<string, object?>(StringComparer.Ordinal);
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            // Key values in group-by order, so they line up with the live result's key.
            foreach (var g in c.GroupBy) key[groupLabels[g.Key]] = DaxValues.Normalize(row.Values[specs.IndexOf(g.Key)]);
            for (var i = 0; i < specs.Count; i++)
                if (measureNames.ContainsKey(specs[i])) values[specs[i]] = DaxValues.Normalize(row.Values[i]);
            var text = LooseKey(key.Values);
            storedOrder.Add(text);
            stored[text] = (key, values);
        }
        var liveByLooseKey = live.Order.ToDictionary(k => LooseKey(live.Rows[k].Key.Values), k => live.Rows[k], StringComparer.Ordinal);

        var diffs = new List<CellDiff>();
        foreach (var k in storedOrder.Concat(liveByLooseKey.Keys.Where(k => !stored.ContainsKey(k))))
        {
            var hasS = stored.TryGetValue(k, out var s);
            var hasL = liveByLooseKey.TryGetValue(k, out var l);
            foreach (var measureKey in specs.Where(measureNames.ContainsKey))
            {
                var sv = hasS ? s.Values.GetValueOrDefault(measureKey) : null;
                var lv = hasL ? l.Values.GetValueOrDefault(measureKey) : null;
                if (!DaxValues.Equal(lv, sv, c.Tolerance))
                    diffs.Add(new CellDiff(hasL ? l.Key : s.Key, measureNames[measureKey], sv, lv, DaxValues.Delta(sv, lv), false));
            }
        }
        return diffs;
    }

    /// <summary>Key text where numbers compare by value whatever their type (a stored 2023 matches a live 2023.0).</summary>
    private static string LooseKey(IEnumerable<object?> values) =>
        string.Join("\u001f", values.Select(v => DaxValues.TryNumber(v, out var d) ? "n:" + d.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : "s:" + DaxValues.Format(v)));
}
