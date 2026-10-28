using System.Globalization;
using System.Text;
using System.Text.Json;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace TabularTools.Core.Queries;

/// <summary>One row of values in a YAML file (snapshots, recordings), written as a flow sequence: <c>[2023, "West", 1234.5]</c>.</summary>
public sealed class YamlRow(IReadOnlyList<object?> values)
{
    public IReadOnlyList<object?> Values { get; } = values;
}

/// <summary>
/// Reads and writes <see cref="YamlRow"/> keeping value types: plain scalars are numbers, booleans or null; quoted
/// scalars are always strings. So <c>"2023"</c> stays text and <c>2023</c> is a number.
/// </summary>
public sealed class YamlRowConverter : IYamlTypeConverter
{
    public static YamlRowConverter Instance { get; } = new();

    public bool Accepts(Type type) => type == typeof(YamlRow);

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        var start = parser.Consume<SequenceStart>();
        var values = new List<object?>();
        while (!parser.TryConsume<SequenceEnd>(out _))
        {
            if (!parser.TryConsume<Scalar>(out var scalar))
                throw new YamlException(start.Start, parser.Current?.End ?? start.End, "Row values must be plain values: numbers, text, true/false or null.");
            values.Add(FromScalar(scalar));
        }
        return new YamlRow(values);
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer) =>
        throw new NotSupportedException("Rows are written by YamlText.");

    public static object? FromScalar(Scalar scalar)
    {
        if (scalar.Style is not ScalarStyle.Plain) return scalar.Value;
        var v = scalar.Value;
        switch (v)
        {
            case "" or "~" or "null" or "Null" or "NULL": return null;
            case "true" or "True" or "TRUE": return true;
            case "false" or "False" or "FALSE": return false;
        }
        if (long.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l)) return l;
        if (!v.Contains('e', StringComparison.OrdinalIgnoreCase) && decimal.TryParse(v, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var m)) return m;
        if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        return v;
    }
}

/// <summary>Hand-rolled YAML output: stable formatting, one row per line, DAX in quoted or literal scalars.</summary>
public static class YamlText
{
    /// <summary>A scalar as YAML: JSON-quoted strings (valid YAML), invariant numbers, true/false, null.</summary>
    public static string Scalar(object? value) => value switch
    {
        null => "null",
        bool b => b ? "true" : "false",
        long l => l.ToString(CultureInfo.InvariantCulture),
        int i => i.ToString(CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        double d when double.IsFinite(d) => d.ToString("R", CultureInfo.InvariantCulture) is var s && (s.Contains('.') || s.Contains('E')) ? s : s + ".0",
        _ => Quote(Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""),
    };

    public static string Quote(string text) => JsonSerializer.Serialize(text, QuoteOptions);

    private static readonly JsonSerializerOptions QuoteOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Row(IEnumerable<object?> values) => "[" + string.Join(", ", values.Select(Scalar)) + "]";

    public static string List(IEnumerable<string> values) => "[" + string.Join(", ", values.Select(Quote)) + "]";

    /// <summary>A literal block (<c>|-</c>) for multi-line text, indented by <paramref name="indent"/> spaces. Falls back to a quoted scalar when a literal can't hold the text.</summary>
    public static string Block(string text, int indent)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n');
        // A literal block can't start with an indented line (it would set the indentation) or keep trailing whitespace reliably.
        if (normalized.Length == 0 || char.IsWhiteSpace(lines[0].FirstOrDefault()) || lines.Any(l => l.Length > 0 && char.IsWhiteSpace(l[^1])))
            return Quote(text);
        var pad = new string(' ', indent);
        var sb = new StringBuilder("|-");
        foreach (var line in lines) sb.Append('\n').Append(line.Length == 0 ? "" : pad + line);
        return sb.ToString();
    }
}
