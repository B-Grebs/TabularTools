using System.Text.Json.Serialization;
using TabularTools.Core.Snapshots;

namespace TabularTools.Core.Dax;

[JsonConverter(typeof(JsonStringEnumConverter<ReferenceKind>))]
public enum ReferenceKind
{
    [JsonStringEnumMemberName("column")] Column,
    [JsonStringEnumMemberName("measure")] Measure,
    [JsonStringEnumMemberName("table")] Table,
    [JsonStringEnumMemberName("function")] Function,
}

/// <summary>Which DAX expression of an object a reference came from.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ExpressionPart>))]
public enum ExpressionPart
{
    [JsonStringEnumMemberName("expression")] Expression,
    [JsonStringEnumMemberName("formatString")] FormatString,
    [JsonStringEnumMemberName("detailRows")] DetailRows,
    [JsonStringEnumMemberName("filter")] Filter,
}

/// <summary>One reference found in a DAX expression.</summary>
/// <param name="Owner">Key of the object whose expression contains the reference.</param>
/// <param name="Part">Which expression of the owner.</param>
/// <param name="Kind">What was referenced.</param>
/// <param name="Table">Table named in the reference, if qualified (or the resolved home table).</param>
/// <param name="Name">Referenced column, measure, table or function name.</param>
/// <param name="FullyQualified">True for <c>Table[Name]</c>, false for <c>[Name]</c>.</param>
/// <param name="Target">Key of the referenced object, or null when it could not be resolved.</param>
/// <param name="Text">Source text of the reference.</param>
/// <param name="Line">1-based line within the expression.</param>
/// <param name="Column">1-based column within the expression.</param>
public sealed record DaxReference(
    string Owner,
    ExpressionPart Part,
    ReferenceKind Kind,
    string? Table,
    string Name,
    bool FullyQualified,
    string? Target,
    string Text,
    int Line,
    int Column)
{
    public bool IsResolved => Target is not null;
}

/// <summary>A non-DAX use of an object: relationship endpoints, sort-by columns and hierarchy levels.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<StructuralUseKind>))]
public enum StructuralUseKind
{
    [JsonStringEnumMemberName("relationship")] Relationship,
    [JsonStringEnumMemberName("sortBy")] SortBy,
    [JsonStringEnumMemberName("hierarchy")] Hierarchy,
}

/// <param name="User">Key of the object that uses <paramref name="Used"/> (relationship, sorted column, hierarchy).</param>
/// <param name="Used">Key of the column being used.</param>
public sealed record StructuralUse(StructuralUseKind Kind, string User, string Used);

/// <summary>Every DAX reference and structural use in a model, indexed both ways.</summary>
public sealed class DependencyGraph
{
    private readonly Dictionary<string, List<DaxReference>> _byOwner;
    private readonly Dictionary<string, List<DaxReference>> _byTarget;
    private readonly Dictionary<string, List<StructuralUse>> _usesOf;

    internal DependencyGraph(IReadOnlyList<DaxReference> references, IReadOnlyList<StructuralUse> structural, IReadOnlyDictionary<string, string> objectTypes)
    {
        References = references;
        StructuralUses = structural;
        ObjectTypes = objectTypes;
        _byOwner = references.GroupBy(r => r.Owner, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        _byTarget = references.Where(r => r.Target is not null).GroupBy(r => r.Target!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        _usesOf = structural.GroupBy(s => s.Used, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
    }

    public IReadOnlyList<DaxReference> References { get; }

    public IReadOnlyList<StructuralUse> StructuralUses { get; }

    /// <summary>Object key → object type (<c>measure</c>, <c>column</c>, ...), for every object in the model.</summary>
    public IReadOnlyDictionary<string, string> ObjectTypes { get; }

    public IEnumerable<DaxReference> Unresolved => References.Where(r => !r.IsResolved);

    /// <summary>References made by an object's expressions.</summary>
    public IReadOnlyList<DaxReference> DependenciesOf(string key) =>
        _byOwner.TryGetValue(key, out var list) ? list : [];

    /// <summary>References to an object from other objects' expressions (excludes self-references).</summary>
    public IReadOnlyList<DaxReference> ReferencesTo(string key) =>
        _byTarget.TryGetValue(key, out var list) ? list.Where(r => r.Owner != key).ToList() : [];

    /// <summary>Relationships, sort-by columns and hierarchies that use a column.</summary>
    public IReadOnlyList<StructuralUse> StructuralUsesOf(string key) =>
        _usesOf.TryGetValue(key, out var list) ? list : [];

    public bool Contains(string key) => ObjectTypes.ContainsKey(key);
}
