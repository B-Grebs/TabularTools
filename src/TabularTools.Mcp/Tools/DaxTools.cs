using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TabularTools.Core.Config;
using TabularTools.Core.Loading;
using TabularTools.Core.Queries;
using TabularTools.Core.Rendering;

namespace TabularTools.Mcp.Tools;

/// <summary>Ad-hoc read-only queries against a live model. Not registered with --disable-live.</summary>
[McpServerToolType]
public sealed class DaxTools(ModelWorkspace workspace, IDaxExecutorFactory executors)
{
    [McpServerTool(Name = "run_dax", Title = "Run a DAX query", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = true,
        UseStructuredContent = true, OutputSchemaType = typeof(DaxResult))]
    [Description("Run a read-only DAX query (EVALUATE, optionally after DEFINE) or a SELECT … FROM $SYSTEM.* DMV against a live semantic model through a named " +
                 "connection profile from tabulartools.yaml, and return the result table. Anything else (TMSL, XMLA, refresh commands) is rejected before connecting. " +
                 "Rows are capped. Results are real model data: aggregate rather than listing detail rows.")]
    public Task<CallToolResult> RunDax(
        [Description("The DAX query, e.g. EVALUATE SUMMARIZECOLUMNS('Date'[Year], \"Sales\", [Total Sales]).")] string query,
        [Description("Connection profile name from tabulartools.yaml. Default: TABULARTOOLS_PROFILE, else tests.profile.")] string? profile = null,
        [Description("Maximum rows to return (1-5000).")] int maxRows = DaxQueryRequest.DefaultMaxRows,
        [Description("Timeout in seconds (1-600).")] int timeoutSeconds = DaxQueryRequest.DefaultTimeoutSeconds,
        [Description("Evaluate as this RLS role (needs admin rights on the model).")] string? role = null) => ToolResults.RunAsync(async () =>
    {
        DaxQueryGuard.Check(query);
        if (maxRows is < 1 or > 5000) throw new ArgumentException("maxRows must be between 1 and 5000.");
        if (timeoutSeconds is < 1 or > 600) throw new ArgumentException("timeoutSeconds must be between 1 and 600.");
        var config = ConfigLoader.Discover(workspace.Roots[0]);
        if (config?.Live.AllowRunDax == false) throw new ArgumentException($"Ad-hoc queries are turned off (live.allowRunDax: false in {config.Path}).");
        var resolved = ConnectionProfiles.Resolve(config, profile);
        var result = await executors.Create(resolved).ExecuteAsync(new DaxQueryRequest(query, role, maxRows, timeoutSeconds)).ConfigureAwait(false);
        return ToolResults.Success(result, DaxResultRenderer.SummaryLine(result));
    });
}
