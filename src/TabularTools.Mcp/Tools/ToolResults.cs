using System.Text.Json;
using ModelContextProtocol.Protocol;
using TabularTools.Core.Loading;
using TabularTools.Core.Rendering;

namespace TabularTools.Mcp.Tools;

/// <summary>Builds tool results: a one-line summary plus the JSON result, as text and as structured content.</summary>
internal static class ToolResults
{
    public static CallToolResult Success<T>(T value, string summary)
    {
        var element = JsonSerializer.SerializeToElement(value, OutputJson.Options);
        return new CallToolResult
        {
            Content =
            [
                new TextContentBlock { Text = summary },
                new TextContentBlock { Text = JsonSerializer.Serialize(element, OutputJson.Options) },
            ],
            StructuredContent = element,
        };
    }

    /// <summary>
    /// Runs a tool body and turns expected failures (bad path, invalid TMDL, bad arguments) into tool errors
    /// the model can read and act on. Unexpected exceptions still surface as server errors.
    /// </summary>
    public static CallToolResult Run(Func<CallToolResult> body)
    {
        try
        {
            return body();
        }
        catch (ModelLoadException ex)
        {
            var lines = ex.Diagnostics.Select(d => "- " + d);
            return Error($"Could not load the model:{Environment.NewLine}{string.Join(Environment.NewLine, lines)}");
        }
        catch (ArgumentException ex)
        {
            return Error(ex.Message);
        }
    }

    public static CallToolResult Error(string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = message }],
    };
}
