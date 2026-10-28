using YamlDotNet.Serialization;

namespace TabularTools.Core.Config;

/// <summary>
/// <c>tabulartools.yaml</c>: committed, team-wide settings. Never holds secrets.
/// The lineage section (M5) is accepted and ignored for now.
/// </summary>
public sealed class TabularToolsConfig
{
    public int SchemaVersion { get; set; } = 1;
    public LintSettings Lint { get; set; } = new();
    /// <summary>Named connections to live models: endpoint, database and auth mode only. Never secrets.</summary>
    public Dictionary<string, ProfileSettings>? Profiles { get; set; }
    public DiffSettings Diff { get; set; } = new();
    public TestSettings Tests { get; set; } = new();
    public LiveSettings Live { get; set; } = new();
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

/// <summary>A connection profile. It names a live model (or a recordings file) and how to authenticate, never a secret.</summary>
public sealed class ProfileSettings
{
    /// <summary>XMLA endpoint: <c>powerbi://api.powerbi.com/v1.0/myorg/Workspace</c>, <c>asazure://…</c>, <c>localhost:port</c>, or <c>desktop</c> (Power BI Desktop, Windows only).</summary>
    public string? Endpoint { get; set; }

    /// <summary>Semantic model (database) name.</summary>
    public string? Database { get; set; }

    /// <summary>default | azureCli | servicePrincipal | interactive | deviceCode | token | none. Default: default.</summary>
    public string? Auth { get; set; }

    /// <summary>For <c>auth: token</c>: name of the environment variable holding an access token.</summary>
    public string? TokenEnv { get; set; }

    /// <summary>Recordings file to replay instead of connecting (offline, for CI). Relative to the config file.</summary>
    public string? Recordings { get; set; }

    public string? Description { get; set; }
}

public sealed class TestSettings
{
    /// <summary>Test file globs relative to the config file, e.g. <c>tests/**/*.measuretests.yaml</c>.</summary>
    public List<string>? Files { get; set; }

    /// <summary>Default connection profile for measure tests.</summary>
    public string? Profile { get; set; }

    /// <summary>Default numeric tolerance (test files can override it).</summary>
    public ToleranceSettings? Tolerance { get; set; }

    /// <summary>Also simulate calculated column changes with <c>DEFINE COLUMN</c>. Off by default: unverified on the service.</summary>
    public bool? ColumnOverrides { get; set; }

    /// <summary>Treat expected changes (on measures the change edited directly) as failures. Default: false.</summary>
    public bool? Strict { get; set; }

    /// <summary>Queries run at the same time. Default: 4.</summary>
    public int? MaxParallel { get; set; }

    /// <summary>Per-query timeout in seconds. Default: 120.</summary>
    public int? TimeoutSeconds { get; set; }
}

/// <summary>Two numbers match when <c>|a − b| ≤ max(absolute, relative × |b|)</c>.</summary>
public sealed class ToleranceSettings
{
    public double? Absolute { get; set; }
    public double? Relative { get; set; }
}

public sealed class LiveSettings
{
    /// <summary>Allow ad-hoc queries (<c>run_dax</c>, CLI <c>dax</c>). Measure tests are unaffected. Default: true.</summary>
    public bool? AllowRunDax { get; set; }

    /// <summary>Extra hosts tokens and queries may go to, besides <c>*.powerbi.com</c> and <c>*.asazure.windows.net</c>. <c>*.example.com</c> matches subdomains.</summary>
    public List<string>? AllowedHosts { get; set; }
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
