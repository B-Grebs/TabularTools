using TabularTools.Core.Lint.Compat;
using TabularTools.Core.Lint.Rules;

namespace TabularTools.Core.Lint;

/// <summary>A named collection of rules: built-in C# rules, or BPA JSON rules.</summary>
public sealed record Ruleset(string Id, string Source, IReadOnlyList<ILintRule> Rules, string? Path = null);

/// <summary>
/// Resolves ruleset specs: <c>builtin</c>, <c>microsoft-bpa</c> (embedded, MIT), or a path to a BPA JSON file
/// (relative paths resolve against <c>baseDirectory</c>, usually the folder of tabulartools.yaml).
/// </summary>
public static class RulesetLoader
{
    public const string Builtin = "builtin";
    public const string MicrosoftBpa = "microsoft-bpa";
    public static readonly IReadOnlyList<string> Defaults = [Builtin, MicrosoftBpa];

    public static Ruleset Load(string spec, string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spec);
        if (spec.Equals(Builtin, StringComparison.OrdinalIgnoreCase))
            return new Ruleset(Builtin, "builtin", BuiltinRules.All);

        if (spec.Equals(MicrosoftBpa, StringComparison.OrdinalIgnoreCase))
            return FromJson(MicrosoftBpa, "embedded", ReadEmbedded("TabularTools.Rules.microsoft-bpa.json"), null);

        if (spec.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || spec.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Ruleset URLs aren't supported yet ({spec}). Download the file and reference its path.");

        var path = Path.GetFullPath(Path.Combine(baseDirectory, spec));
        if (!File.Exists(path)) throw new ArgumentException($"Rules file not found: {path}");
        return FromJson(spec, "file", File.ReadAllText(path), path);
    }

    public static Ruleset FromJson(string id, string source, string json, string? path) =>
        new(id, source, BpaRuleFile.Parse(json, path ?? id).Select(d => (ILintRule)new BpaRule(d, id)).ToList(), path);

    private static string ReadEmbedded(string name)
    {
        using var stream = typeof(RulesetLoader).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded ruleset '{name}' is missing from the build.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
