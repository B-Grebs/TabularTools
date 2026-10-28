using System.ComponentModel;
using ModelContextProtocol.Server;
using TabularTools.Core.Lint;
using TabularTools.Core.Loading;
using TabularTools.Core.Rendering;

namespace TabularTools.Mcp.Tools;

/// <summary>Rulesets as MCP resources, so agents can read rule definitions without running lint.</summary>
[McpServerResourceType]
public sealed class RulesetResources(ModelWorkspace workspace)
{
    [McpServerResource(UriTemplate = "tabulartools://rulesets", Name = "rulesets", Title = "Lint rulesets", MimeType = "application/json")]
    [Description("The rulesets lint_model runs by default (built-in TT rules and Microsoft's Best Practice Analyzer rules), with rule counts.")]
    public string List()
    {
        var rulesets = RulesetLoader.Defaults.Select(id => RulesetLoader.Load(id, workspace.Roots[0]));
        return OutputJson.Serialize(rulesets.Select(r => new RulesetSummary(r.Id, r.Source, r.Rules.Count, r.Path)));
    }

    [McpServerResource(UriTemplate = "tabulartools://rulesets/{id}", Name = "ruleset", Title = "Lint ruleset", MimeType = "application/json")]
    [Description("Every rule in a ruleset: id, name, category, severity, scope, description and, for BPA rules, the Dynamic LINQ expression. id is 'builtin' or 'microsoft-bpa'.")]
    public string Get(string id)
    {
        if (!RulesetLoader.Defaults.Contains(id, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"Unknown ruleset '{id}'. Available: {string.Join(", ", RulesetLoader.Defaults)}.");
        var ruleset = RulesetLoader.Load(id, workspace.Roots[0]);
        return OutputJson.Serialize(new { ruleset.Id, ruleset.Source, Rules = ruleset.Rules.Select(r => r.Info) });
    }
}
