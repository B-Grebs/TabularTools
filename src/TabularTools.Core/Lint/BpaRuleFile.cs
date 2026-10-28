using System.Text.Json;
using System.Text.Json.Serialization;

namespace TabularTools.Core.Lint;

/// <summary>A rule as written in a Best Practice Analyzer JSON file (Tabular Editor format).</summary>
public sealed class BpaRuleDefinition
{
    public string? ID { get; set; }
    public string? Name { get; set; }
    public string? Category { get; set; }
    public string? Description { get; set; }
    public int Severity { get; set; } = 2;
    public string? Scope { get; set; }
    public string? Expression { get; set; }
    public string? FixExpression { get; set; }
    public int? CompatibilityLevel { get; set; }
    public string? Remarks { get; set; }
    public string? Source { get; set; }

    /// <summary>Optional TabularTools extension; Tabular Editor ignores it.</summary>
    [JsonPropertyName("x-tabulartools")]
    public BpaRuleExtension? Extension { get; set; }
}

public sealed class BpaRuleExtension
{
    public string? Severity { get; set; }
    public List<string>? Tags { get; set; }
    public string? HelpUrl { get; set; }
}

public static class BpaRuleFile
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>Parses a BPA rules file: a JSON array of rules (a single rule object is also accepted).</summary>
    public static IReadOnlyList<BpaRuleDefinition> Parse(string json, string sourceName)
    {
        try
        {
            json = json.TrimStart('﻿');
            var trimmed = json.TrimStart();
            var rules = trimmed.StartsWith('{')
                ? [JsonSerializer.Deserialize<BpaRuleDefinition>(json, Options)!]
                : JsonSerializer.Deserialize<List<BpaRuleDefinition>>(json, Options) ?? [];
            return rules;
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"Rules file '{sourceName}' is not valid BPA JSON: {ex.Message}", ex);
        }
    }
}
