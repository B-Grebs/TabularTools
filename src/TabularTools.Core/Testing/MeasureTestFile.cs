using System.Text;
using System.Text.RegularExpressions;
using TabularTools.Core.Config;
using TabularTools.Core.Queries;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace TabularTools.Core.Testing;

/// <summary>A measure regression test file, <c>*.measuretests.yaml</c> (ARCHITECTURE §11.4).</summary>
public sealed class MeasureTestFile
{
    public const string Extension = ".measuretests.yaml";
    public const string SchemaUrl = "https://raw.githubusercontent.com/B-Grebs/TabularTools/main/schemas/measure-tests.schema.json";

    public int SchemaVersion { get; set; } = 1;

    /// <summary>The semantic model, relative to this file: where head and base definitions come from.</summary>
    public string? Model { get; set; }

    /// <summary>Connection profile name from tabulartools.yaml. Never a connection string.</summary>
    public string? Profile { get; set; }

    public TestDefaults? Defaults { get; set; }

    public List<TestCase> Cases { get; set; } = [];

    /// <summary>Absolute path the file was loaded from.</summary>
    [YamlIgnore] public string? Path { get; set; }

    [YamlIgnore] public string Directory => System.IO.Path.GetDirectoryName(Path ?? System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "x"))!;

    /// <summary>Absolute path of the model.</summary>
    [YamlIgnore] public string ModelPath => System.IO.Path.GetFullPath(System.IO.Path.Combine(Directory, Model ?? "."));

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithTypeConverter(YamlRowConverter.Instance)
        .Build();

    private static readonly Regex IdPattern = new("^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$", RegexOptions.CultureInvariant);

    public static MeasureTestFile Load(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        if (!File.Exists(full)) throw new TestFileException($"Test file not found: {full}");
        var file = Parse(File.ReadAllText(full), full);
        file.Path = full;
        return file;
    }

    public static MeasureTestFile Parse(string yaml, string sourceName)
    {
        MeasureTestFile? file;
        try
        {
            file = Deserializer.Deserialize<MeasureTestFile?>(yaml);
        }
        catch (YamlException ex)
        {
            var where = ex.Start.Line > 0 ? $"({ex.Start.Line},{ex.Start.Column})" : "";
            throw new TestFileException($"{sourceName}{where}: {(ex.InnerException ?? ex).Message}", ex);
        }
        file ??= new MeasureTestFile();
        file.Cases ??= [];
        Validate(file, sourceName);
        return file;
    }

    private static void Validate(MeasureTestFile file, string where)
    {
        if (file.SchemaVersion != 1) throw new TestFileException($"{where}: unsupported schemaVersion {file.SchemaVersion} (expected 1).");
        if (string.IsNullOrWhiteSpace(file.Model)) throw new TestFileException($"{where}: 'model' is required: the semantic model folder, relative to this file.");
        CheckTolerance(file.Defaults?.Tolerance, $"{where}: defaults.tolerance");
        CheckTexts(file.Defaults?.Filters, $"{where}: defaults.filters");

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < file.Cases.Count; i++)
        {
            var c = file.Cases[i] ?? throw new TestFileException($"{where}: cases[{i}] is empty.");
            var at = $"{where}: cases[{i}]";
            if (string.IsNullOrWhiteSpace(c.Id) || !IdPattern.IsMatch(c.Id))
                throw new TestFileException($"{at}: 'id' is required: letters, digits, '.', '-' and '_' (got '{c.Id}').");
            at = $"{where}: case '{c.Id}'";
            if (!ids.Add(c.Id)) throw new TestFileException($"{at}: duplicate id.");
            if (c.Measures is not { Count: > 0 }) throw new TestFileException($"{at}: 'measures' needs at least one measure.");
            CheckTexts(c.Measures, $"{at}: measures");
            CheckTexts(c.GroupBy, $"{at}: groupBy");
            CheckTexts(c.Filters, $"{at}: filters");
            CheckTolerance(c.Tolerance, $"{at}: tolerance");
            if (c.AsRole is not null && string.IsNullOrWhiteSpace(c.AsRole)) throw new TestFileException($"{at}: asRole is empty.");

            foreach (var (a, k) in (c.Assertions ?? []).Select((a, k) => (a, k)))
            {
                if (a is null) throw new TestFileException($"{at}: assertions[{k}] is empty.");
                var kind = AssertionKinds.Parse(a.Type, $"{at}: assertions[{k}]");
                switch (kind)
                {
                    case AssertionKind.Invariant when string.IsNullOrWhiteSpace(a.Expr):
                        throw new TestFileException($"{at}: invariant assertions need 'expr', a DAX condition that must hold on every row.");
                    case AssertionKind.Snapshot:
                        if (a.Columns is not { Count: > 0 }) throw new TestFileException($"{at}: snapshot assertions need 'columns'.");
                        if (a.Rows is null) throw new TestFileException($"{at}: snapshot assertions need 'rows' (use [] for no rows).");
                        foreach (var (row, r) in a.Rows.Select((row, r) => (row, r)))
                            if (row.Values.Count != a.Columns.Count)
                                throw new TestFileException($"{at}: snapshot row {r + 1} has {row.Values.Count} values for {a.Columns.Count} columns.");
                        break;
                }
                if (kind != AssertionKind.Invariant && a.Expr is not null) throw new TestFileException($"{at}: 'expr' only applies to invariant assertions.");
                if (kind != AssertionKind.Snapshot && (a.Columns is not null || a.Rows is not null || a.CapturedAt is not null))
                    throw new TestFileException($"{at}: 'columns', 'rows' and 'capturedAt' only apply to snapshot assertions.");
            }
        }
    }

    private static void CheckTexts(List<string>? values, string where)
    {
        if (values is null) return;
        for (var i = 0; i < values.Count; i++)
            if (string.IsNullOrWhiteSpace(values[i])) throw new TestFileException($"{where}[{i}] is empty.");
    }

    private static void CheckTolerance(ToleranceSettings? t, string where)
    {
        if (t is null) return;
        if (t.Absolute < 0 || t.Relative < 0) throw new TestFileException($"{where}: values can't be negative.");
    }

    /// <summary>The whole file as YAML. Comments in an existing file aren't kept; see <see cref="MeasureTestGenerator"/>.</summary>
    public string ToYaml()
    {
        var sb = new StringBuilder();
        sb.Append("# yaml-language-server: $schema=").Append(SchemaUrl).Append('\n');
        sb.Append("schemaVersion: 1\n");
        sb.Append("model: ").Append(YamlText.Quote(Model ?? "")).Append('\n');
        if (Profile is not null) sb.Append("profile: ").Append(YamlText.Quote(Profile)).Append('\n');
        if (Defaults is { } d && (d.Tolerance is not null || d.Filters is { Count: > 0 }))
        {
            sb.Append("defaults:\n");
            if (d.Tolerance is not null) sb.Append("  tolerance: ").Append(ToleranceYaml(d.Tolerance)).Append('\n');
            if (d.Filters is { Count: > 0 })
            {
                sb.Append("  filters:\n");
                foreach (var f in d.Filters) sb.Append("    - ").Append(YamlText.Quote(f)).Append('\n');
            }
        }
        sb.Append("cases:");
        if (Cases.Count == 0) sb.Append(" []");
        sb.Append('\n');
        foreach (var c in Cases) sb.Append(CaseYaml(c));
        return sb.ToString();
    }

    /// <summary>One case as a YAML list item under <c>cases:</c> (two-space indent).</summary>
    public static string CaseYaml(TestCase c)
    {
        ArgumentNullException.ThrowIfNull(c);
        var sb = new StringBuilder();
        sb.Append("  - id: ").Append(PlainId(c.Id)).Append('\n');
        if (c.Description is not null) sb.Append("    description: ").Append(YamlText.Quote(c.Description)).Append('\n');
        sb.Append("    measures: ").Append(YamlText.List(c.Measures ?? [])).Append('\n');
        if (c.GroupBy is { Count: > 0 }) sb.Append("    groupBy: ").Append(YamlText.List(c.GroupBy)).Append('\n');
        if (c.Filters is { Count: > 0 }) sb.Append("    filters: ").Append(YamlText.List(c.Filters)).Append('\n');
        if (c.AsRole is not null) sb.Append("    asRole: ").Append(YamlText.Quote(c.AsRole)).Append('\n');
        if (c.Tolerance is not null) sb.Append("    tolerance: ").Append(ToleranceYaml(c.Tolerance)).Append('\n');
        if (c.Assertions is { Count: > 0 })
        {
            sb.Append("    assertions:\n");
            foreach (var a in c.Assertions)
            {
                sb.Append("      - type: ").Append(a.Type).Append('\n');
                if (a.Description is not null) sb.Append("        description: ").Append(YamlText.Quote(a.Description)).Append('\n');
                if (a.Expr is not null) sb.Append("        expr: ").Append(YamlText.Quote(a.Expr)).Append('\n');
                if (a.CapturedAt is not null) sb.Append("        capturedAt: ").Append(YamlText.Quote(a.CapturedAt)).Append('\n');
                if (a.Columns is not null) sb.Append("        columns: ").Append(YamlText.List(a.Columns)).Append('\n');
                if (a.Rows is not null)
                {
                    sb.Append("        rows:");
                    if (a.Rows.Count == 0) sb.Append(" []");
                    sb.Append('\n');
                    foreach (var row in a.Rows) sb.Append("          - ").Append(YamlText.Row(row.Values)).Append('\n');
                }
            }
        }
        return sb.ToString();
    }

    private static readonly Regex PlainIdPattern = new("^[A-Za-z][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase) { "null", "true", "false", "yes", "no", "on", "off", "y", "n" };

    private static string PlainId(string? id) => id is not null && PlainIdPattern.IsMatch(id) && !Reserved.Contains(id) ? id : YamlText.Quote(id ?? "");

    private static string ToleranceYaml(ToleranceSettings t)
    {
        var parts = new List<string>();
        if (t.Absolute is { } a) parts.Add("absolute: " + YamlText.Scalar(a));
        if (t.Relative is { } r) parts.Add("relative: " + YamlText.Scalar(r));
        return "{ " + string.Join(", ", parts) + " }";
    }
}

public sealed class TestDefaults
{
    public ToleranceSettings? Tolerance { get; set; }

    /// <summary>DAX filters applied to every case, e.g. <c>'Date'[Year] IN { 2023, 2024 }</c>. Pin closed periods.</summary>
    public List<string>? Filters { get; set; }
}

public sealed class TestCase
{
    public string? Id { get; set; }
    public string? Description { get; set; }

    /// <summary>Measures to evaluate: <c>[Total Sales]</c> or <c>Sales[Total Sales]</c>.</summary>
    public List<string>? Measures { get; set; }

    /// <summary>Columns to slice by: <c>'Date'[Year]</c>.</summary>
    public List<string>? GroupBy { get; set; }

    /// <summary>DAX filter arguments for CALCULATETABLE, added to the defaults.</summary>
    public List<string>? Filters { get; set; }

    /// <summary>Evaluate under this RLS role (needs admin rights on the model).</summary>
    public string? AsRole { get; set; }

    public ToleranceSettings? Tolerance { get; set; }

    /// <summary>Default: one differential assertion.</summary>
    public List<TestAssertion>? Assertions { get; set; }
}

public sealed class TestAssertion
{
    /// <summary>differential | invariant | snapshot.</summary>
    public string? Type { get; set; }
    public string? Description { get; set; }

    /// <summary>Invariant: a DAX condition that must be true on every row, e.g. <c>[Margin %] &lt;= 1</c>.</summary>
    public string? Expr { get; set; }

    /// <summary>Snapshot: when the values were captured (ISO 8601).</summary>
    public string? CapturedAt { get; set; }

    /// <summary>Snapshot: the group-by columns and measures, in row order.</summary>
    public List<string>? Columns { get; set; }

    /// <summary>Snapshot: expected rows, one per line.</summary>
    public List<YamlRow>? Rows { get; set; }
}

public enum AssertionKind { Differential, Invariant, Snapshot }

public static class AssertionKinds
{
    public static AssertionKind Parse(string? text, string where) => text?.Trim().ToLowerInvariant() switch
    {
        "differential" => AssertionKind.Differential,
        "invariant" => AssertionKind.Invariant,
        "snapshot" => AssertionKind.Snapshot,
        _ => throw new TestFileException($"{where}: type '{text}' isn't differential, invariant or snapshot."),
    };

    public static string Name(AssertionKind kind) => kind.ToString().ToLowerInvariant();
}

/// <summary>An invalid test file. Treated as a usage error.</summary>
public sealed class TestFileException(string message, Exception? inner = null) : ArgumentException(message, inner);
