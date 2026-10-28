using YamlDotNet.Serialization;

namespace TabularTools.Core.Config;

/// <summary>
/// <c>tabulartools.yaml</c>: committed, team-wide settings. Never holds secrets.
/// Sections for later milestones (profiles, tests, lineage) are accepted and ignored for now.
/// </summary>
public sealed class TabularToolsConfig
{
    public int SchemaVersion { get; set; } = 1;
    public LintSettings Lint { get; set; } = new();
    public object? Profiles { get; set; }
    public DiffSettings Diff { get; set; } = new();
    public object? Tests { get; set; }
    public object? Lineage { get; set; }

    /// <summary>Path of the file this was loaded from (null for defaults).</summary>
    [YamlIgnore] public string? Path { get; set; }

    /// <summary>Folder relative paths in the config resolve against.</summary>
    [YamlIgnore] public string? Directory => Path is null ? null : System.IO.Path.GetDirectoryName(Path);
}

public sealed class LintSettings
{
    /// <summary>Ruleset specs: <c>builtin</c>, <c>microsoft-bpa</c>, or paths to BPA JSON files. Default: builtin + microsoft-bpa.</summary>
    public List<string>? Rulesets { get; set; }

    /// <summary>error | warning | info | none. Default: error.</summary>
    public string? FailOn { get; set; }

    /// <summary>Per-rule severity overrides: error, warning, info, or off to disable.</summary>
    public Dictionary<string, string>? Severity { get; set; }

    /// <summary>Rule ids that never run.</summary>
    public List<string>? Disabled { get; set; }

    public List<SuppressionSetting>? Suppress { get; set; }

    /// <summary>Also run rules embedded in the model's BestPracticeAnalyzer annotation (Tabular Editor).</summary>
    public bool? UseModelRules { get; set; }
}

public sealed class DiffSettings
{
    /// <summary>breaking | high | medium | low | info | none. Default: none (diff is informational).</summary>
    public string? FailOn { get; set; }

    /// <summary>Minimum similarity (0–1) for a heuristic rename. Default: 0.8.</summary>
    public double? RenameThreshold { get; set; }

    /// <summary>Also report expression changes that only touch whitespace or comments. Default: false.</summary>
    public bool? IncludeFormattingOnly { get; set; }

    /// <summary>Per-change-kind severity overrides, e.g. <c>column.summarizeByChanged: low</c>.</summary>
    public Dictionary<string, string>? Severity { get; set; }
}

/// <summary>Hide a rule's violations on matching objects. A reason is required.</summary>
public sealed class SuppressionSetting
{
    /// <summary>Rule id; <c>*</c> wildcards allowed.</summary>
    public string? Rule { get; set; }

    /// <summary>Glob on object keys, e.g. <c>table:Staging*/**</c>. <c>*</c> stays within a path segment, <c>**</c> crosses them. Default: every object.</summary>
    public string? Objects { get; set; }

    public string? Reason { get; set; }
}

/// <summary>An invalid or unreadable config file. Treated as a usage error.</summary>
public sealed class ConfigException(string message, Exception? inner = null) : ArgumentException(message, inner);
