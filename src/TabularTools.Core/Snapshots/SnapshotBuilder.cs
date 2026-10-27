using TOM = Microsoft.AnalysisServices.Tabular;

namespace TabularTools.Core.Snapshots;

/// <summary>Builds an immutable <see cref="ModelSnapshot"/> from a TOM database.</summary>
public static class SnapshotBuilder
{
    public static ModelSnapshot Build(TOM.Database database)
    {
        ArgumentNullException.ThrowIfNull(database);
        var model = database.Model;

        var tables = model.Tables.Select(BuildTable).ToList();

        var relationships = model.Relationships
            .OfType<TOM.SingleColumnRelationship>()
            .Select(r => new RelationshipSnapshot(
                ObjectKeys.Relationship(r.FromTable.Name, r.FromColumn.Name, r.ToTable.Name, r.ToColumn.Name),
                r.Name,
                r.FromTable.Name, r.FromColumn.Name, r.ToTable.Name, r.ToColumn.Name,
                Camel(r.FromCardinality), Camel(r.ToCardinality),
                Camel(r.CrossFilteringBehavior), Camel(r.SecurityFilteringBehavior),
                r.IsActive, r.RelyOnReferentialIntegrity))
            .ToList();

        var roles = model.Roles.Select(role => new RoleSnapshot(
                ObjectKeys.Role(role.Name),
                role.Name,
                Camel(role.ModelPermission),
                Null(role.Description),
                role.TablePermissions.Select(tp => new TablePermissionSnapshot(
                        tp.Table.Name,
                        Null(tp.FilterExpression),
                        Camel(tp.MetadataPermission),
                        tp.ColumnPermissions.Select(cp => new ColumnPermissionSnapshot(cp.Column.Name, Camel(cp.MetadataPermission))).ToList()))
                    .ToList(),
                role.Members.Count))
            .ToList();

        var expressions = model.Expressions.Select(e => new NamedExpressionSnapshot(
                ObjectKeys.Expression(e.Name),
                e.Name,
                e.Kind == TOM.ExpressionKind.M ? "m" : "dax",
                e.Expression ?? "",
                IsParameter(e),
                Null(e.Description),
                Null(e.LineageTag)))
            .ToList();

        var functions = model.Functions.Select(f => new FunctionSnapshot(
                ObjectKeys.Function(f.Name), f.Name, f.Expression ?? "", Null(f.Description), f.IsHidden, Null(f.LineageTag)))
            .ToList();

        return new ModelSnapshot(
            string.IsNullOrEmpty(database.Name) ? model.Name : database.Name,
            database.CompatibilityLevel,
            Null(model.Culture),
            Camel(model.DefaultMode),
            model.DiscourageImplicitMeasures,
            tables,
            relationships,
            roles,
            expressions,
            functions,
            model.Perspectives.Select(p => p.Name).ToList(),
            model.Cultures.Select(c => c.Name).ToList());
    }

    private static TableSnapshot BuildTable(TOM.Table t)
    {
        var kind = t.CalculationGroup is not null ? TableKind.CalculationGroup
            : t.Partitions.Any(p => p.SourceType == TOM.PartitionSourceType.Calculated) ? TableKind.CalculatedTable
            : TableKind.Table;

        var calcTableExpression = kind == TableKind.CalculatedTable
            ? t.Partitions.Select(p => p.Source).OfType<TOM.CalculatedPartitionSource>().FirstOrDefault()?.Expression
            : null;

        var columns = t.Columns
            .Where(c => c.Type != TOM.ColumnType.RowNumber)
            .Select(c => new ColumnSnapshot(
                ObjectKeys.Column(t.Name, c.Name),
                t.Name,
                c.Name,
                c.Type switch
                {
                    TOM.ColumnType.Calculated => ColumnKind.Calculated,
                    TOM.ColumnType.CalculatedTableColumn => ColumnKind.CalculatedTableColumn,
                    TOM.ColumnType.RowNumber => ColumnKind.RowNumber,
                    _ => ColumnKind.Data,
                },
                Camel(c.DataType),
                Camel(c.SummarizeBy),
                Null(c.FormatString),
                c.IsHidden,
                c.IsKey,
                Null(c.DisplayFolder),
                Null(c.Description),
                Null(c.DataCategory),
                c.SortByColumn?.Name,
                c switch
                {
                    TOM.DataColumn d => Null(d.SourceColumn),
                    TOM.CalculatedTableColumn ct => Null(ct.SourceColumn),
                    _ => null,
                },
                (c as TOM.CalculatedColumn)?.Expression,
                Null(c.LineageTag)))
            .ToList();

        var measures = t.Measures.Select(m => new MeasureSnapshot(
                ObjectKeys.Measure(t.Name, m.Name),
                t.Name,
                m.Name,
                m.Expression ?? "",
                Null(m.FormatString),
                Null(m.FormatStringDefinition?.Expression),
                Null(m.DisplayFolder),
                Null(m.Description),
                m.IsHidden,
                m.KPI is not null,
                Null(m.DetailRowsDefinition?.Expression),
                Null(m.LineageTag)))
            .ToList();

        var partitions = t.Partitions.Select(p => new PartitionSnapshot(
                ObjectKeys.Partition(t.Name, p.Name),
                t.Name,
                p.Name,
                Camel(p.SourceType),
                Camel(p.Mode),
                p.Source switch
                {
                    TOM.MPartitionSource m => m.Expression,
                    TOM.QueryPartitionSource q => q.Query,
                    TOM.CalculatedPartitionSource c => c.Expression,
                    TOM.EntityPartitionSource e => e.EntityName,
                    _ => null,
                },
                p.Source switch
                {
                    TOM.QueryPartitionSource q => q.DataSource?.Name,
                    TOM.EntityPartitionSource e => e.ExpressionSource?.Name ?? e.DataSource?.Name,
                    _ => null,
                }))
            .ToList();

        var hierarchies = t.Hierarchies.Select(h => new HierarchySnapshot(
                ObjectKeys.Hierarchy(t.Name, h.Name),
                t.Name,
                h.Name,
                h.IsHidden,
                Null(h.DisplayFolder),
                Null(h.Description),
                h.Levels.OrderBy(l => l.Ordinal).Select(l => new LevelSnapshot(l.Name, l.Ordinal, l.Column.Name)).ToList(),
                Null(h.LineageTag)))
            .ToList();

        var calcItems = t.CalculationGroup?.CalculationItems
            .Select(ci => new CalculationItemSnapshot(
                ObjectKeys.CalculationItem(t.Name, ci.Name),
                t.Name,
                ci.Name,
                ci.Expression ?? "",
                ci.Ordinal,
                Null(ci.FormatStringDefinition?.Expression),
                Null(ci.Description)))
            .ToList() ?? [];

        return new TableSnapshot(
            ObjectKeys.Table(t.Name),
            t.Name,
            kind,
            t.IsHidden,
            Null(t.Description),
            Null(t.DataCategory),
            Null(t.LineageTag),
            calcTableExpression,
            t.CalculationGroup?.Precedence,
            columns,
            measures,
            partitions,
            hierarchies,
            calcItems);
    }

    private static bool IsParameter(TOM.NamedExpression e) =>
        e.Kind == TOM.ExpressionKind.M &&
        e.Expression is { } text &&
        text.Contains("IsParameterQuery=true", StringComparison.OrdinalIgnoreCase);

    private static string? Null(string? value) => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>Enum value as camelCase, matching TMDL spelling (e.g. <c>bothDirections</c>).</summary>
    internal static string Camel<T>(T value) where T : struct, Enum
    {
        var s = value.ToString();
        return s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];
    }
}
