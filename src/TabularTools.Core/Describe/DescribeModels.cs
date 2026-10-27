using System.ComponentModel;
using System.Text.Json.Serialization;
using TabularTools.Core.Loading;

namespace TabularTools.Core.Describe;

/// <summary>How much to return from describe.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DetailLevel>))]
public enum DetailLevel
{
    /// <summary>Counts and a table list only.</summary>
    [JsonStringEnumMemberName("summary")] Summary,
    /// <summary>Every matching object with its main properties, no expressions.</summary>
    [JsonStringEnumMemberName("objects")] Objects,
    /// <summary>Objects plus DAX and M expressions and RLS filters.</summary>
    [JsonStringEnumMemberName("full")] Full,
}

/// <summary>Object types describe can list.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ObjectType>))]
public enum ObjectType
{
    [JsonStringEnumMemberName("table")] Table,
    [JsonStringEnumMemberName("column")] Column,
    [JsonStringEnumMemberName("measure")] Measure,
    [JsonStringEnumMemberName("partition")] Partition,
    [JsonStringEnumMemberName("hierarchy")] Hierarchy,
    [JsonStringEnumMemberName("calculationItem")] CalculationItem,
    [JsonStringEnumMemberName("relationship")] Relationship,
    [JsonStringEnumMemberName("role")] Role,
    [JsonStringEnumMemberName("expression")] Expression,
    [JsonStringEnumMemberName("function")] Function,
}

public sealed record DescribeRequest
{
    public DetailLevel Detail { get; init; } = DetailLevel.Objects;
    public IReadOnlyList<string>? Tables { get; init; }
    public IReadOnlyList<ObjectType>? ObjectTypes { get; init; }
    /// <summary>Glob on object names: <c>*</c> and <c>?</c>, case-insensitive.</summary>
    public string? NamePattern { get; init; }
    public bool IncludeHidden { get; init; } = true;
    public int Limit { get; init; } = 200;
    public string? Cursor { get; init; }
}

public sealed record ModelCounts(
    int Tables, int Columns, int Measures, int Relationships, int Roles, int CalculationGroups,
    int CalculationItems, int Hierarchies, int Partitions, int Expressions, int Functions);

public sealed record ModelInfo(
    string Name,
    ModelSourceKind SourceKind,
    ModelFormat Format,
    string InputPath,
    string DefinitionPath,
    int CompatibilityLevel,
    ModelCounts Counts,
    string? Culture = null,
    string? Handle = null);

public sealed record TableSummary(string Name, string Kind, bool IsHidden, int Columns, int Measures, int Partitions, string? Description = null);

/// <summary>One model object. <see cref="Properties"/> carries type-specific fields.</summary>
public sealed record DescribedObject
{
    public required string Key { get; init; }
    public required string Type { get; init; }
    public string? Table { get; init; }
    public required string Name { get; init; }
    public bool? IsHidden { get; init; }
    public string? DataType { get; init; }
    public string? SummarizeBy { get; init; }
    public string? FormatString { get; init; }
    public string? DisplayFolder { get; init; }
    public string? Description { get; init; }
    public string? LineageTag { get; init; }
    [Description("DAX or M expression. Only returned with detail 'full'.")]
    public string? Expression { get; init; }
    public IReadOnlyDictionary<string, object?>? Properties { get; init; }
}

public sealed record DescribeResult
{
    public required ModelInfo Model { get; init; }
    public required DetailLevel Detail { get; init; }
    public IReadOnlyList<TableSummary>? Tables { get; init; }
    public IReadOnlyList<DescribedObject>? Objects { get; init; }
    public int TotalObjects { get; init; }
    public bool Truncated { get; init; }
    public string? NextCursor { get; init; }
}
