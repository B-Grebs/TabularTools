using TOM = Microsoft.AnalysisServices.Tabular;

namespace TabularTools.Core.Lint.Compat;

/// <summary>One reference inside a DAX expression (Tabular Editor's ObjectReference).</summary>
public sealed class TeObjectReference(bool fullyQualified, string text)
{
    public bool FullyQualified { get; } = fullyQualified;
    public string Text { get; } = text;
}

/// <summary>Referenced object and every occurrence of it (Tabular Editor's DependsOn dictionary entry).</summary>
public sealed class TeDependsOnEntry(TeObject key, List<TeObjectReference> value)
{
    public TeObject Key { get; } = key;
    public List<TeObjectReference> Value { get; } = value;
}

/// <summary>What an object's DAX references, grouped by referenced object.</summary>
public sealed class TeDependsOn : List<TeDependsOnEntry>
{
    public static TeDependsOn Empty => [];

    public List<TeMeasure> Measures => this.Select(e => e.Key).OfType<TeMeasure>().ToList();
    public List<TeColumn> Columns => this.Select(e => e.Key).OfType<TeColumn>().ToList();
    public List<TeTable> Tables => this.Select(e => e.Key).OfType<TeTable>().ToList();

    internal static TeDependsOn For(TeModel model, string key)
    {
        var result = new TeDependsOn();
        foreach (var group in model.Graph.DependenciesOf(key).Where(r => r.IsResolved).GroupBy(r => r.Target!, StringComparer.Ordinal))
        {
            if (model.Find(group.Key) is { } target)
                result.Add(new TeDependsOnEntry(target, group.Select(r => new TeObjectReference(r.FullyQualified, r.Text)).ToList()));
        }
        return result;
    }
}

/// <summary>Objects whose DAX references this object (directly). <see cref="AllMeasures"/> follows references transitively.</summary>
public sealed class TeReferencedBy : List<TeObject>
{
    private TeModel? _model;
    private string? _key;

    public List<TeMeasure> Measures => this.OfType<TeMeasure>().ToList();
    public List<TeColumn> Columns => this.OfType<TeColumn>().ToList();
    public List<TeTable> Tables => this.OfType<TeTable>().ToList();

    /// <summary>Measures that reference this object directly or through other objects.</summary>
    public List<TeMeasure> AllMeasures
    {
        get
        {
            if (_model is null || _key is null) return [];
            var seen = new HashSet<string>(StringComparer.Ordinal) { _key };
            var queue = new Queue<string>([_key]);
            var result = new List<TeMeasure>();
            while (queue.Count > 0)
            {
                foreach (var owner in _model.Graph.ReferencesTo(queue.Dequeue()).Select(r => r.Owner).Distinct())
                {
                    if (!seen.Add(owner)) continue;
                    queue.Enqueue(owner);
                    if (_model.Find(owner) is TeMeasure m) result.Add(m);
                }
            }
            return result;
        }
    }

    internal static TeReferencedBy For(TeModel model, string key)
    {
        var result = new TeReferencedBy { _model = model, _key = key };
        foreach (var owner in model.Graph.ReferencesTo(key).Select(r => r.Owner).Distinct(StringComparer.Ordinal))
            if (model.Find(owner) is { } o) result.Add(o);
        return result;
    }
}

/// <summary>Values per role (or table), enumerable and indexable by name. Missing names give null.</summary>
public class TeNameIndexer<T>(IEnumerable<(string Name, T Value)> values) : List<T>(values.Select(v => v.Value))
    where T : class
{
    private readonly Dictionary<string, T> _byName = values.GroupBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.OrdinalIgnoreCase);

    public T? this[string name] => _byName.TryGetValue(name, out var v) ? v : null;
}

/// <summary>RLS filter expressions; only non-empty filters are enumerated.</summary>
public sealed class TeRlsIndexer(IEnumerable<(string Name, string Value)> values)
    : TeNameIndexer<string>(values.Where(v => !string.IsNullOrWhiteSpace(v.Value)));

/// <summary>Whether a table is included in a perspective, by perspective name.</summary>
public sealed class TePerspectiveIndexer(TeModel model, TOM.Table table)
{
    public bool this[string perspective] =>
        table.Model.Perspectives.Find(perspective) is { } p && p.PerspectiveTables.Find(table.Name) is not null;

    public bool Any() => table.Model.Perspectives.Any(p => p.PerspectiveTables.Find(table.Name) is not null);

    internal TeModel Model { get; } = model;
}

/// <summary>Tabular Editor exposes .NET's Regex as <c>RegEx</c>. Patterns time out after a second.</summary>
public static class RegEx
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);

    public static bool IsMatch(string? input, string pattern) =>
        System.Text.RegularExpressions.Regex.IsMatch(input ?? "", pattern, System.Text.RegularExpressions.RegexOptions.None, Timeout);

    public static bool IsMatch(string? input, string pattern, System.Text.RegularExpressions.RegexOptions options) =>
        System.Text.RegularExpressions.Regex.IsMatch(input ?? "", pattern, options, Timeout);

    public static string Replace(string? input, string pattern, string replacement) =>
        System.Text.RegularExpressions.Regex.Replace(input ?? "", pattern, replacement, System.Text.RegularExpressions.RegexOptions.None, Timeout);
}

/// <summary>A DAX token as Tabular Editor's <c>Tokenize()</c> returns it: type name, text, and neighbours.</summary>
public sealed class TeToken
{
    internal TeToken(string type, string text) { Type = type; Text = text; }

    public string Type { get; }
    public string Text { get; }
    public TeToken? Next { get; internal set; }
    public TeToken? Previous { get; internal set; }

    public override string ToString() => Text;

    /// <summary>Significant tokens (no whitespace or comments), linked to their neighbours.</summary>
    internal static List<TeToken> From(string? expression)
    {
        var tokens = Dax.DaxLexer.Significant(expression).Select(t => new TeToken(t.Type.ToString(), t.Text)).ToList();
        for (var i = 0; i < tokens.Count; i++)
        {
            tokens[i].Previous = i > 0 ? tokens[i - 1] : null;
            tokens[i].Next = i + 1 < tokens.Count ? tokens[i + 1] : null;
        }
        return tokens;
    }
}
