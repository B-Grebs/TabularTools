namespace TabularTools.Core.Diff;

/// <summary>One change kind with its default severity. Documented in docs/diff-severities.md.</summary>
public sealed record ChangeKindInfo(string Kind, ChangeSeverity Severity, string Description);

/// <summary>
/// Default severity per change kind (ARCHITECTURE §10.5). Teams override single kinds in
/// <c>tabulartools.yaml</c> under <c>diff.severity</c>.
/// </summary>
public static class SeverityPolicy
{
    private const ChangeSeverity B = ChangeSeverity.Breaking, H = ChangeSeverity.High, M = ChangeSeverity.Medium, L = ChangeSeverity.Low, I = ChangeSeverity.Info;

    public static IReadOnlyList<ChangeKindInfo> Kinds { get; } =
    [
        // Model
        new("model.compatibilityLevelChanged", M, "Compatibility level changed."),
        new("model.discourageImplicitMeasuresChanged", M, "Discourage implicit measures switched on or off."),
        new("model.defaultModeChanged", H, "Default storage mode changed (import, DirectQuery, ...)."),
        new("model.cultureChanged", L, "Model culture changed."),
        new("perspective.added", L, "Perspective added."),
        new("perspective.removed", L, "Perspective removed."),
        new("culture.added", L, "Translation culture added."),
        new("culture.removed", L, "Translation culture removed."),

        // Tables
        new("table.added", I, "Table added."),
        new("table.removed", B, "Table removed; reports and DAX that use it break."),
        new("table.renamed", B, "Table renamed; reports bind by name."),
        new("table.kindChanged", H, "Table changed between regular, calculated and calculation group."),
        new("table.expressionChanged", H, "Calculated table DAX changed."),
        new("table.expressionFormatting", I, "Calculated table DAX reformatted (whitespace or comments only)."),
        new("table.hidden", M, "Table hidden."),
        new("table.unhidden", L, "Table unhidden."),
        new("table.descriptionChanged", L, "Table description changed."),
        new("table.dataCategoryChanged", L, "Table data category changed (e.g. marked as a date table)."),
        new("calculationGroup.precedenceChanged", H, "Calculation group precedence changed."),

        // Columns
        new("column.added", I, "Column added."),
        new("column.removed", B, "Column removed."),
        new("column.renamed", B, "Column renamed."),
        new("column.kindChanged", H, "Column changed between data and calculated."),
        new("column.dataTypeChanged", H, "Column data type changed."),
        new("column.keyDataTypeChanged", B, "Data type changed on a column used by a relationship."),
        new("column.expressionChanged", H, "Calculated column DAX changed."),
        new("column.expressionFormatting", I, "Calculated column DAX reformatted (whitespace or comments only)."),
        new("column.sourceColumnChanged", H, "Column now loads from a different source column."),
        new("column.summarizeByChanged", M, "Default summarization changed."),
        new("column.sortByChanged", M, "Sort-by column changed."),
        new("column.isKeyChanged", M, "Key column flag changed."),
        new("column.hidden", M, "Column hidden."),
        new("column.unhidden", L, "Column unhidden."),
        new("column.formatStringChanged", L, "Column format string changed."),
        new("column.displayFolderChanged", L, "Column display folder changed."),
        new("column.descriptionChanged", L, "Column description changed."),
        new("column.dataCategoryChanged", L, "Column data category changed."),

        // Measures
        new("measure.added", I, "Measure added."),
        new("measure.removed", B, "Measure removed."),
        new("measure.renamed", B, "Measure renamed."),
        new("measure.moved", B, "Measure moved to another table; report field references include the table."),
        new("measure.expressionChanged", H, "Measure DAX changed."),
        new("measure.expressionFormatting", I, "Measure DAX reformatted (whitespace or comments only)."),
        new("measure.formatStringExpressionChanged", M, "Dynamic format string expression changed."),
        new("measure.detailRowsChanged", M, "Detail rows expression changed."),
        new("measure.kpiChanged", L, "KPI added or removed."),
        new("measure.hidden", M, "Measure hidden."),
        new("measure.unhidden", L, "Measure unhidden."),
        new("measure.formatStringChanged", L, "Measure format string changed."),
        new("measure.displayFolderChanged", L, "Measure display folder changed."),
        new("measure.descriptionChanged", L, "Measure description changed."),

        // Partitions
        new("partition.added", H, "Partition added to an existing table."),
        new("partition.removed", H, "Partition removed from an existing table."),
        new("partition.sourceChanged", H, "Partition source (M, SQL or DAX) changed."),
        new("partition.sourceFormatting", I, "Partition source reformatted (whitespace or comments only)."),
        new("partition.sourceTypeChanged", H, "Partition source type changed."),
        new("partition.modeChanged", H, "Partition storage mode changed."),

        // Hierarchies
        new("hierarchy.added", I, "Hierarchy added."),
        new("hierarchy.removed", B, "Hierarchy removed."),
        new("hierarchy.renamed", B, "Hierarchy renamed."),
        new("hierarchy.levelsChanged", M, "Hierarchy levels changed."),
        new("hierarchy.hidden", M, "Hierarchy hidden."),
        new("hierarchy.unhidden", L, "Hierarchy unhidden."),
        new("hierarchy.displayFolderChanged", L, "Hierarchy display folder changed."),
        new("hierarchy.descriptionChanged", L, "Hierarchy description changed."),

        // Calculation items
        new("calculationItem.added", I, "Calculation item added."),
        new("calculationItem.removed", B, "Calculation item removed."),
        new("calculationItem.renamed", B, "Calculation item renamed."),
        new("calculationItem.expressionChanged", H, "Calculation item DAX changed."),
        new("calculationItem.expressionFormatting", I, "Calculation item DAX reformatted (whitespace or comments only)."),
        new("calculationItem.ordinalChanged", H, "Calculation item ordinal changed."),
        new("calculationItem.formatStringExpressionChanged", M, "Calculation item format string expression changed."),
        new("calculationItem.descriptionChanged", L, "Calculation item description changed."),

        // Relationships (matched by endpoints)
        new("relationship.added", M, "Relationship added."),
        new("relationship.removed", H, "Relationship removed."),
        new("relationship.crossFilterChanged", H, "Cross-filter direction changed."),
        new("relationship.cardinalityChanged", H, "Cardinality changed."),
        new("relationship.activeChanged", H, "Relationship made active or inactive."),
        new("relationship.securityFilteringChanged", H, "Security filtering behaviour changed."),
        new("relationship.referentialIntegrityChanged", M, "'Assume referential integrity' changed (DirectQuery joins)."),

        // Roles and security
        new("role.added", H, "Role added."),
        new("role.removed", B, "Role removed."),
        new("role.modelPermissionChanged", B, "Role model permission changed."),
        new("role.rlsFilterAdded", B, "Row-level security filter added."),
        new("role.rlsFilterChanged", B, "Row-level security filter changed."),
        new("role.rlsFilterFormatting", I, "Row-level security filter reformatted (whitespace or comments only)."),
        new("role.rlsFilterRemoved", B, "Row-level security filter removed."),
        new("role.olsChanged", B, "Object-level security (table or column permission) changed."),
        new("role.membersChanged", H, "Number of role members changed."),
        new("role.descriptionChanged", L, "Role description changed."),

        // Shared expressions and parameters
        new("expression.added", I, "Shared expression or parameter added."),
        new("expression.removed", H, "Shared expression or parameter removed; partitions that use it break."),
        new("expression.renamed", H, "Shared expression or parameter renamed."),
        new("expression.expressionChanged", H, "Shared expression or parameter value changed."),
        new("expression.expressionFormatting", I, "Shared expression reformatted (whitespace or comments only)."),
        new("expression.kindChanged", H, "Shared expression changed between M and DAX."),
        new("expression.descriptionChanged", L, "Shared expression description changed."),

        // DAX user-defined functions
        new("function.added", I, "DAX function added."),
        new("function.removed", B, "DAX function removed."),
        new("function.renamed", B, "DAX function renamed."),
        new("function.expressionChanged", H, "DAX function body changed."),
        new("function.expressionFormatting", I, "DAX function reformatted (whitespace or comments only)."),
        new("function.hidden", M, "DAX function hidden."),
        new("function.unhidden", L, "DAX function unhidden."),
        new("function.descriptionChanged", L, "DAX function description changed."),
    ];

    private static readonly Dictionary<string, ChangeKindInfo> ByKind = Kinds.ToDictionary(k => k.Kind, StringComparer.Ordinal);

    public static bool IsKnown(string kind) => ByKind.ContainsKey(kind);

    public static ChangeSeverity Default(string kind) =>
        ByKind.TryGetValue(kind, out var info) ? info.Severity : throw new ArgumentException($"Unknown change kind '{kind}'.");

    /// <summary>Default severity, unless the config overrides this kind.</summary>
    public static ChangeSeverity Resolve(string kind, IReadOnlyDictionary<string, ChangeSeverity>? overrides) =>
        overrides is not null && overrides.TryGetValue(kind, out var s) ? s : Default(kind);
}
