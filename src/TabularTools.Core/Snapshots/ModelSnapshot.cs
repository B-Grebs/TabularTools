namespace TabularTools.Core.Snapshots;

// Immutable, engine-friendly view of a semantic model. Built once per load from TOM.
// Every object carries a stable Key (see ObjectKeys) that diff, lint and describe share.

public sealed record ModelSnapshot(
    string Name,
    int CompatibilityLevel,
    string? Culture,
    string? DefaultMode,
    bool DiscourageImplicitMeasures,
    IReadOnlyList<TableSnapshot> Tables,
    IReadOnlyList<RelationshipSnapshot> Relationships,
    IReadOnlyList<RoleSnapshot> Roles,
    IReadOnlyList<NamedExpressionSnapshot> Expressions,
    IReadOnlyList<FunctionSnapshot> Functions,
    IReadOnlyList<string> Perspectives,
    IReadOnlyList<string> Cultures)
{
    public IEnumerable<ColumnSnapshot> AllColumns => Tables.SelectMany(t => t.Columns);
    public IEnumerable<MeasureSnapshot> AllMeasures => Tables.SelectMany(t => t.Measures);
    public IEnumerable<PartitionSnapshot> AllPartitions => Tables.SelectMany(t => t.Partitions);
    public IEnumerable<HierarchySnapshot> AllHierarchies => Tables.SelectMany(t => t.Hierarchies);
    public IEnumerable<CalculationItemSnapshot> AllCalculationItems => Tables.SelectMany(t => t.CalculationItems);
}

/// <summary>Plain table, calculated table (DAX), or calculation group.</summary>
public enum TableKind { Table, CalculatedTable, CalculationGroup }

public sealed record TableSnapshot(
    string Key,
    string Name,
    TableKind Kind,
    bool IsHidden,
    string? Description,
    string? DataCategory,
    string? LineageTag,
    string? Expression,
    int? CalculationGroupPrecedence,
    IReadOnlyList<ColumnSnapshot> Columns,
    IReadOnlyList<MeasureSnapshot> Measures,
    IReadOnlyList<PartitionSnapshot> Partitions,
    IReadOnlyList<HierarchySnapshot> Hierarchies,
    IReadOnlyList<CalculationItemSnapshot> CalculationItems);

/// <summary>Data column (from source), calculated column (DAX), calculated-table column, or the hidden row-number column.</summary>
public enum ColumnKind { Data, Calculated, CalculatedTableColumn, RowNumber }

public sealed record ColumnSnapshot(
    string Key,
    string Table,
    string Name,
    ColumnKind Kind,
    string DataType,
    string SummarizeBy,
    string? FormatString,
    bool IsHidden,
    bool IsKey,
    string? DisplayFolder,
    string? Description,
    string? DataCategory,
    string? SortByColumn,
    string? SourceColumn,
    string? Expression,
    string? LineageTag);

public sealed record MeasureSnapshot(
    string Key,
    string Table,
    string Name,
    string Expression,
    string? FormatString,
    string? FormatStringExpression,
    string? DisplayFolder,
    string? Description,
    bool IsHidden,
    bool HasKpi,
    string? DetailRowsExpression,
    string? LineageTag);

public sealed record PartitionSnapshot(
    string Key,
    string Table,
    string Name,
    string SourceType,
    string Mode,
    string? Expression,
    string? DataSource);

public sealed record HierarchySnapshot(
    string Key,
    string Table,
    string Name,
    bool IsHidden,
    string? DisplayFolder,
    string? Description,
    IReadOnlyList<LevelSnapshot> Levels,
    string? LineageTag);

public sealed record LevelSnapshot(string Name, int Ordinal, string Column);

public sealed record CalculationItemSnapshot(
    string Key,
    string Table,
    string Name,
    string Expression,
    int Ordinal,
    string? FormatStringExpression,
    string? Description);

public sealed record RelationshipSnapshot(
    string Key,
    string Name,
    string FromTable,
    string FromColumn,
    string ToTable,
    string ToColumn,
    string FromCardinality,
    string ToCardinality,
    string CrossFilteringBehavior,
    string SecurityFilteringBehavior,
    bool IsActive,
    bool RelyOnReferentialIntegrity);

public sealed record RoleSnapshot(
    string Key,
    string Name,
    string ModelPermission,
    string? Description,
    IReadOnlyList<TablePermissionSnapshot> TablePermissions,
    int MemberCount);

public sealed record TablePermissionSnapshot(
    string Table,
    string? FilterExpression,
    string MetadataPermission,
    IReadOnlyList<ColumnPermissionSnapshot> ColumnPermissions);

public sealed record ColumnPermissionSnapshot(string Column, string MetadataPermission);

public sealed record NamedExpressionSnapshot(
    string Key,
    string Name,
    string Kind,
    string Expression,
    bool IsParameter,
    string? Description,
    string? LineageTag);

public sealed record FunctionSnapshot(
    string Key,
    string Name,
    string Expression,
    string? Description,
    bool IsHidden,
    string? LineageTag);
