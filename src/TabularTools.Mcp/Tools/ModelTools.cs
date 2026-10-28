using System.ComponentModel;
using System.Runtime.InteropServices;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TabularTools.Core;
using TabularTools.Core.Describe;
using TabularTools.Core.Diagnostics;
using TabularTools.Core.Loading;

namespace TabularTools.Mcp.Tools;

/// <summary>Read-only tools for loading and inspecting semantic models.</summary>
[McpServerToolType]
public sealed class ModelTools(ModelWorkspace workspace, ServerOptions options)
{
    internal const string ModelParameterDescription =
        "Model to use: a .pbip file, a *.SemanticModel folder, a TMDL definition folder, a model.bim file, or a modelId handle from open_model. " +
        "Relative paths resolve against the server root. Omit when the root contains exactly one model.";

    [McpServerTool(Name = "ping", Title = "Server info", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false, UseStructuredContent = true,
        OutputSchemaType = typeof(PingResult))]
    [Description("Return the TabularTools server version, the Analysis Services (TOM) library version, the folders model paths resolve against, and whether write tools are enabled. Use to check the server is running.")]
    public CallToolResult Ping()
    {
        var result = new PingResult(ProductInfo.Name, ProductInfo.Version, ProductInfo.TomVersion, workspace.Roots,
            options.AllowWrites, !options.DisableLive, RuntimeInformation.OSDescription, RuntimeInformation.FrameworkDescription);
        return ToolResults.Success(result, $"{ProductInfo.Name} {ProductInfo.Version} (TOM {ProductInfo.TomVersion}) on {result.Platform}.");
    }

    [McpServerTool(Name = "open_model", Title = "Open semantic model", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false, UseStructuredContent = true,
        OutputSchemaType = typeof(OpenModelResult))]
    [Description("Load a Power BI semantic model from a PBIP project, a *.SemanticModel folder, a TMDL definition folder, or a model.bim file. " +
                 "Returns a modelId handle plus a summary (object counts, compatibility level, load diagnostics). " +
                 "Call this first to check a model parses; other tools also accept the path directly. Invalid TMDL is reported with file and line.")]
    public CallToolResult OpenModel([Description(ModelParameterDescription)] string? path = null) => ToolResults.Run(() =>
    {
        var opened = workspace.Open(path);
        var info = ModelDescriber.Info(opened.Model, opened.Handle);
        var result = new OpenModelResult(opened.Handle, info, opened.Model.Diagnostics);
        var c = info.Counts;
        return ToolResults.Success(result,
            $"Opened '{info.Name}' as {opened.Handle}: {c.Tables} tables, {c.Columns} columns, {c.Measures} measures, {c.Relationships} relationships, {c.Roles} roles.");
    });

    [McpServerTool(Name = "describe_model", Title = "Describe semantic model", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false, UseStructuredContent = true,
        OutputSchemaType = typeof(DescribeResult))]
    [Description("List the contents of a semantic model: tables, columns, measures, relationships, roles, calculation items, partitions, hierarchies, M parameters and DAX functions. " +
                 "Start with detail 'summary' to see tables and counts, then narrow with tables / objectTypes / namePattern. " +
                 "Use detail 'full' to read DAX and M expressions and RLS filters. Large results are paged: pass nextCursor back as cursor.")]
    public CallToolResult DescribeModel(
        [Description(ModelParameterDescription)] string? model = null,
        [Description("'summary' = counts and table list; 'objects' = objects and properties without expressions; 'full' = objects plus DAX/M expressions and RLS filters.")]
        DetailLevel detail = DetailLevel.Objects,
        [Description("Only objects in these tables (case-insensitive). Relationships match either end.")] string[]? tables = null,
        [Description("Only these object types.")] ObjectType[]? objectTypes = null,
        [Description("Glob on object names, e.g. 'Sales*' or '*Key'. Case-insensitive.")] string? namePattern = null,
        [Description("Include hidden objects.")] bool includeHidden = true,
        [Description("Maximum objects per page (1-1000).")] int limit = 200,
        [Description("nextCursor from the previous page.")] string? cursor = null) => ToolResults.Run(() =>
    {
        var opened = workspace.Open(model);
        var result = ModelDescriber.Describe(opened.Model, new DescribeRequest
        {
            Detail = detail,
            Tables = tables,
            ObjectTypes = objectTypes,
            NamePattern = namePattern,
            IncludeHidden = includeHidden,
            Limit = limit,
            Cursor = cursor,
        }, opened.Handle);

        var summary = detail == DetailLevel.Summary
            ? $"'{result.Model.Name}': {result.Tables!.Count} tables."
            : $"'{result.Model.Name}': {result.Objects!.Count} of {result.TotalObjects} matching objects" +
              (result.Truncated ? $"; more available with cursor '{result.NextCursor}'." : ".");
        return ToolResults.Success(result, summary);
    });
}

public sealed record PingResult(
    string Name, string Version, string TomVersion, IReadOnlyList<string> Roots, bool WritesEnabled, bool LiveEnabled, string Platform, string Runtime);

public sealed record OpenModelResult(string ModelId, ModelInfo Model, IReadOnlyList<Diagnostic> Diagnostics);
