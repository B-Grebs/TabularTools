using TabularTools.Core.Lint;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace TabularTools.Core.Config;

/// <summary>Finds and reads <c>tabulartools.yaml</c>, merging an optional git-ignored <c>tabulartools.local.yaml</c> next to it.</summary>
public static class ConfigLoader
{
    public const string FileName = "tabulartools.yaml";
    public const string LocalFileName = "tabulartools.local.yaml";

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    /// <summary>Walks up from <paramref name="startDirectory"/> to the repo root (a folder containing .git) looking for tabulartools.yaml.</summary>
    public static TabularToolsConfig? Discover(string startDirectory)
    {
        var dir = new DirectoryInfo(Path.GetFullPath(startDirectory));
        if (!dir.Exists) dir = dir.Parent;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, FileName);
            if (File.Exists(candidate)) return Load(candidate);
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) || File.Exists(Path.Combine(dir.FullName, ".git"))) break;
            dir = dir.Parent;
        }
        return null;
    }

    public static TabularToolsConfig Load(string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new ConfigException($"Config file not found: {full}");
        var config = Parse(File.ReadAllText(full), full);
        config.Path = full;

        var local = Path.Combine(Path.GetDirectoryName(full)!, LocalFileName);
        if (File.Exists(local)) Merge(config, Parse(File.ReadAllText(local), local));

        Validate(config);
        return config;
    }

    public static TabularToolsConfig Parse(string yaml, string sourceName)
    {
        try
        {
            return Deserializer.Deserialize<TabularToolsConfig?>(yaml) ?? new TabularToolsConfig();
        }
        catch (YamlException ex)
        {
            var where = ex.Start.Line > 0 ? $"({ex.Start.Line},{ex.Start.Column})" : "";
            throw new ConfigException($"{sourceName}{where}: {(ex.InnerException ?? ex).Message}", ex);
        }
    }

    /// <summary>Local values win: scalars and lists replace, severity maps merge, suppressions are appended.</summary>
    private static void Merge(TabularToolsConfig target, TabularToolsConfig local)
    {
        var t = target.Lint;
        var l = local.Lint;
        t.Rulesets = l.Rulesets ?? t.Rulesets;
        t.FailOn = l.FailOn ?? t.FailOn;
        t.Disabled = l.Disabled ?? t.Disabled;
        t.UseModelRules = l.UseModelRules ?? t.UseModelRules;
        if (l.Severity is not null)
        {
            t.Severity ??= [];
            foreach (var (k, v) in l.Severity) t.Severity[k] = v;
        }
        if (l.Suppress is not null) t.Suppress = [.. t.Suppress ?? [], .. l.Suppress];
    }

    private static void Validate(TabularToolsConfig config)
    {
        var where = config.Path ?? FileName;
        if (config.SchemaVersion != 1) throw new ConfigException($"{where}: unsupported schemaVersion {config.SchemaVersion} (expected 1).");
        var lint = config.Lint;
        if (lint.FailOn is not null)
        {
            try { SeverityParsing.ParseFailOn(lint.FailOn); }
            catch (ArgumentException ex) { throw new ConfigException($"{where}: lint.failOn: {ex.Message}"); }
        }
        foreach (var (rule, value) in lint.Severity ?? [])
            if (!value.Equals("off", StringComparison.OrdinalIgnoreCase) && !SeverityParsing.TryParse(value, out _))
                throw new ConfigException($"{where}: lint.severity.{rule}: '{value}' isn't error, warning, info or off.");
        foreach (var s in lint.Suppress ?? [])
        {
            if (string.IsNullOrWhiteSpace(s.Rule)) throw new ConfigException($"{where}: every lint.suppress entry needs a rule.");
            if (string.IsNullOrWhiteSpace(s.Reason)) throw new ConfigException($"{where}: lint.suppress for '{s.Rule}' needs a reason.");
        }
    }
}
