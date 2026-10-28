using TabularTools.Core.Snapshots;

namespace TabularTools.Core.Diff;

/// <summary>
/// Best-effort index from object keys to the TMDL file and line that declares them, found by scanning declarations.
/// TMSL (<c>model.bim</c>) models get no locations.
/// </summary>
public sealed class SourceLocationIndex
{
    private readonly Dictionary<string, SourceLocation> _byKey;

    private SourceLocationIndex(Dictionary<string, SourceLocation> byKey) => _byKey = byKey;

    public static SourceLocationIndex Empty { get; } = new(new Dictionary<string, SourceLocation>(StringComparer.Ordinal));

    public SourceLocation? Find(string key) => _byKey.GetValueOrDefault(key);

    public int Count => _byKey.Count;

    /// <summary>Builds the index from TMDL files.</summary>
    /// <param name="files">Display path (repo-relative) → file text. Non-TMDL files are ignored.</param>
    /// <param name="snapshot">The model, used to turn relationship names into endpoint keys.</param>
    public static SourceLocationIndex Build(IEnumerable<KeyValuePair<string, string>> files, ModelSnapshot snapshot)
    {
        var byKey = new Dictionary<string, SourceLocation>(StringComparer.Ordinal);
        var relationshipKeys = snapshot.Relationships.ToDictionary(r => r.Name, r => r.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var (path, text) in files.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            if (!path.EndsWith(".tmdl", StringComparison.OrdinalIgnoreCase)) continue;
            string? table = null, role = null;
            var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var raw = lines[i];
                var depth = 0;
                while (depth < raw.Length && raw[depth] == '\t') depth++;
                var line = raw[depth..];
                if (line.Length == 0 || line.StartsWith("///", StringComparison.Ordinal)) continue;

                var (keyword, rest) = SplitKeyword(line);
                var location = new SourceLocation(path, i + 1);

                if (depth == 0)
                {
                    table = role = null;
                    switch (keyword)
                    {
                        case "table": table = ReadName(rest); Add(byKey, ObjectKeys.Table(table), location); break;
                        case "role": role = ReadName(rest); Add(byKey, ObjectKeys.Role(role), location); break;
                        case "expression": Add(byKey, ObjectKeys.Expression(ReadName(rest)), location); break;
                        case "function": Add(byKey, ObjectKeys.Function(ReadName(rest)), location); break;
                        case "perspective": Add(byKey, ObjectKeys.Perspective(ReadName(rest)), location); break;
                        case "cultureInfo": Add(byKey, ObjectKeys.Culture(ReadName(rest)), location); break;
                        case "model": Add(byKey, ObjectKeys.Model, location); break;
                        case "relationship":
                            if (relationshipKeys.TryGetValue(ReadName(rest), out var relKey)) Add(byKey, relKey, location);
                            break;
                    }
                }
                else if (depth == 1 && table is not null)
                {
                    switch (keyword)
                    {
                        case "measure": Add(byKey, ObjectKeys.Measure(table, ReadName(rest)), location); break;
                        case "column": Add(byKey, ObjectKeys.Column(table, ReadName(rest)), location); break;
                        case "partition": Add(byKey, ObjectKeys.Partition(table, ReadName(rest)), location); break;
                        case "hierarchy": Add(byKey, ObjectKeys.Hierarchy(table, ReadName(rest)), location); break;
                    }
                }
                else if (depth == 2 && table is not null && keyword == "calculationItem")
                {
                    Add(byKey, ObjectKeys.CalculationItem(table, ReadName(rest)), location);
                }
                else if (depth == 1 && role is not null && keyword == "tablePermission")
                {
                    Add(byKey, ObjectKeys.TablePermission(role, ReadName(rest)), location);
                }
            }
        }
        return new SourceLocationIndex(byKey);
    }

    private static void Add(Dictionary<string, SourceLocation> map, string key, SourceLocation location) => map.TryAdd(key, location);

    private static (string Keyword, string Remainder) SplitKeyword(string line)
    {
        var space = line.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 ? (line, "") : (line[..space], line[(space + 1)..]);
    }

    /// <summary>Reads a TMDL object name: <c>'Quoted ''name'''</c> or a bare word, up to <c>=</c> or whitespace.</summary>
    internal static string ReadName(string text)
    {
        text = text.TrimStart();
        if (text.StartsWith('\''))
        {
            var sb = new System.Text.StringBuilder();
            for (var i = 1; i < text.Length; i++)
            {
                if (text[i] == '\'')
                {
                    if (i + 1 < text.Length && text[i + 1] == '\'') { sb.Append('\''); i++; continue; }
                    break;
                }
                sb.Append(text[i]);
            }
            return sb.ToString();
        }
        var end = 0;
        while (end < text.Length && !char.IsWhiteSpace(text[end]) && text[end] != '=') end++;
        return text[..end];
    }
}
