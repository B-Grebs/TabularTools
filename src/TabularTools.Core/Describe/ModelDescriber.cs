using System.Text;
using System.Text.RegularExpressions;
using TabularTools.Core.Loading;
using TabularTools.Core.Snapshots;

namespace TabularTools.Core.Describe;

/// <summary>Lists a model's contents at a chosen level of detail, with filters and paging.</summary>
public static class ModelDescriber
{
    public const int MaxLimit = 1000;

    public static ModelInfo Info(LoadedModel model, string? handle = null)
    {
        var s = model.Snapshot;
        var counts = new ModelCounts(
            s.Tables.Count,
            s.AllColumns.Count(),
            s.AllMeasures.Count(),
            s.Relationships.Count,
            s.Roles.Count,
            s.Tables.Count(t => t.Kind == TableKind.CalculationGroup),
            s.AllCalculationItems.Count(),
            s.AllHierarchies.Count(),
            s.AllPartitions.Count(),
            s.Expressions.Count,
            s.Functions.Count);
        return new ModelInfo(s.Name, handle, model.Source.Kind, model.Source.Format, model.Source.InputPath,
            model.Source.DefinitionPath, s.CompatibilityLevel, s.Culture, counts);
    }

    public static DescribeResult Describe(LoadedModel model, DescribeRequest request, string? handle = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var info = Info(model, handle);
        var s = model.Snapshot;
        var tableFilter = request.Tables is { Count: > 0 } ? new HashSet<string>(request.Tables, StringComparer.OrdinalIgnoreCase) : null;

        if (request.Detail == DetailLevel.Summary)
        {
            var tables = s.Tables
                .Where(t => tableFilter is null || tableFilter.Contains(t.Name))
                .Where(t => request.IncludeHidden || !t.IsHidden)
                .Select(t => new TableSummary(t.Name, Camel(t.Kind), t.IsHidden, t.Columns.Count, t.Measures.Count, t.Partitions.Count, t.Description))
                .ToList();
            return new DescribeResult { Model = info, Detail = request.Detail, Tables = tables, TotalObjects = tables.Count };
        }

        var full = request.Detail == DetailLevel.Full;
        var types = request.ObjectTypes is { Count: > 0 } ? request.ObjectTypes.ToHashSet() : null;
        var name = request.NamePattern is { Length: > 0 } p ? GlobToRegex(p) : null;

        var all = Enumerate(s, full, tableFilter)
            .Where(o => types is null || types.Contains(o.ObjectType))
            .Where(o => request.IncludeHidden || o.Object.IsHidden != true)
            .Where(o => name is null || name.IsMatch(o.Object.Name))
            .Select(o => o.Object)
            .ToList();

        var limit = Math.Clamp(request.Limit, 1, MaxLimit);
        var offset = DecodeCursor(request.Cursor);
        var page = all.Skip(offset).Take(limit).ToList();
        var truncated = offset + page.Count < all.Count;

        return new DescribeResult
        {
            Model = info,
            Detail = request.Detail,
            Objects = page,
            TotalObjects = all.Count,
            Truncated = truncated,
            NextCursor = truncated ? EncodeCursor(offset + page.Count) : null,
        };
    }

    private static IEnumerable<(ObjectType ObjectType, DescribedObject Object)> Enumerate(ModelSnapshot s, bool full, HashSet<string>? tableFilter)
    {
        foreach (var t in s.Tables)
        {
            if (tableFilter is not null && !tableFilter.Contains(t.Name)) continue;

            yield return (ObjectType.Table, new DescribedObject
            {
                Key = t.Key, Type = "table", Name = t.Name, IsHidden = t.IsHidden, Description = t.Description, LineageTag = t.LineageTag,
                Expression = full ? t.Expression : null,
                Properties = Props(
                    ("kind", Camel(t.Kind)), ("dataCategory", t.DataCategory),
                    ("columns", t.Columns.Count), ("measures", t.Measures.Count),
                    ("calculationGroupPrecedence", t.CalculationGroupPrecedence)),
            });

            foreach (var c in t.Columns)
                yield return (ObjectType.Column, new DescribedObject
                {
                    Key = c.Key, Type = "column", Table = c.Table, Name = c.Name, IsHidden = c.IsHidden, DataType = c.DataType,
                    SummarizeBy = c.SummarizeBy, FormatString = c.FormatString, DisplayFolder = c.DisplayFolder,
                    Description = c.Description, LineageTag = c.LineageTag, Expression = full ? c.Expression : null,
                    Properties = Props(("kind", Camel(c.Kind)), ("isKey", c.IsKey ? true : null), ("sortByColumn", c.SortByColumn),
                        ("sourceColumn", c.SourceColumn), ("dataCategory", c.DataCategory)),
                });

            foreach (var m in t.Measures)
                yield return (ObjectType.Measure, new DescribedObject
                {
                    Key = m.Key, Type = "measure", Table = m.Table, Name = m.Name, IsHidden = m.IsHidden, FormatString = m.FormatString,
                    DisplayFolder = m.DisplayFolder, Description = m.Description, LineageTag = m.LineageTag,
                    Expression = full ? m.Expression : null,
                    Properties = Props(("hasKpi", m.HasKpi ? true : null),
                        ("formatStringExpression", full ? m.FormatStringExpression : null),
                        ("detailRowsExpression", full ? m.DetailRowsExpression : null)),
                });

            foreach (var h in t.Hierarchies)
                yield return (ObjectType.Hierarchy, new DescribedObject
                {
                    Key = h.Key, Type = "hierarchy", Table = h.Table, Name = h.Name, IsHidden = h.IsHidden, DisplayFolder = h.DisplayFolder,
                    Description = h.Description, LineageTag = h.LineageTag,
                    Properties = Props(("levels", h.Levels.Select(l => l.Name == l.Column ? l.Name : $"{l.Name} ({l.Column})").ToList())),
                });

            foreach (var ci in t.CalculationItems)
                yield return (ObjectType.CalculationItem, new DescribedObject
                {
                    Key = ci.Key, Type = "calculationItem", Table = ci.Table, Name = ci.Name, Description = ci.Description,
                    Expression = full ? ci.Expression : null,
                    Properties = Props(("ordinal", ci.Ordinal), ("formatStringExpression", full ? ci.FormatStringExpression : null)),
                });

            foreach (var p in t.Partitions)
                yield return (ObjectType.Partition, new DescribedObject
                {
                    Key = p.Key, Type = "partition", Table = p.Table, Name = p.Name,
                    Expression = full ? p.Expression : null,
                    Properties = Props(("sourceType", p.SourceType), ("mode", p.Mode), ("dataSource", p.DataSource)),
                });
        }

        foreach (var r in s.Relationships)
        {
            if (tableFilter is not null && !tableFilter.Contains(r.FromTable) && !tableFilter.Contains(r.ToTable)) continue;
            yield return (ObjectType.Relationship, new DescribedObject
            {
                Key = r.Key, Type = "relationship", Name = r.Name,
                Properties = Props(
                    ("from", $"{Quote(r.FromTable)}[{r.FromColumn}]"), ("to", $"{Quote(r.ToTable)}[{r.ToColumn}]"),
                    ("cardinality", $"{r.FromCardinality}:{r.ToCardinality}"),
                    ("crossFilteringBehavior", r.CrossFilteringBehavior), ("securityFilteringBehavior", r.SecurityFilteringBehavior),
                    ("isActive", r.IsActive), ("relyOnReferentialIntegrity", r.RelyOnReferentialIntegrity ? true : null)),
            });
        }

        foreach (var role in s.Roles)
        {
            if (tableFilter is not null && !role.TablePermissions.Any(tp => tableFilter.Contains(tp.Table))) continue;
            object filters = full
                ? role.TablePermissions.Select(tp => new Dictionary<string, object?>
                {
                    ["table"] = tp.Table,
                    ["filterExpression"] = tp.FilterExpression,
                    ["metadataPermission"] = tp.MetadataPermission == "default" ? null : tp.MetadataPermission,
                    ["columnPermissions"] = tp.ColumnPermissions.Count == 0 ? null : tp.ColumnPermissions.Select(cp => $"{cp.Column}: {cp.MetadataPermission}").ToList(),
                }).ToList()
                : role.TablePermissions.Select(tp => tp.Table).ToList();
            yield return (ObjectType.Role, new DescribedObject
            {
                Key = role.Key, Type = "role", Name = role.Name, Description = role.Description,
                Properties = Props(("modelPermission", role.ModelPermission), ("members", role.MemberCount), ("tablePermissions", filters)),
            });
        }

        if (tableFilter is not null) yield break;

        foreach (var e in s.Expressions)
            yield return (ObjectType.Expression, new DescribedObject
            {
                Key = e.Key, Type = "expression", Name = e.Name, Description = e.Description, LineageTag = e.LineageTag,
                Expression = full ? e.Expression : null,
                Properties = Props(("kind", e.Kind), ("isParameter", e.IsParameter ? true : null)),
            });

        foreach (var f in s.Functions)
            yield return (ObjectType.Function, new DescribedObject
            {
                Key = f.Key, Type = "function", Name = f.Name, IsHidden = f.IsHidden, Description = f.Description, LineageTag = f.LineageTag,
                Expression = full ? f.Expression : null,
            });
    }

    private static Dictionary<string, object?>? Props(params (string Name, object? Value)[] values)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (n, v) in values)
            if (v is not null) dict[n] = v;
        return dict.Count == 0 ? null : dict;
    }

    internal static string Quote(string table) =>
        table.All(ch => char.IsLetterOrDigit(ch) || ch == '_') && !char.IsDigit(table[0]) ? table : $"'{table.Replace("'", "''")}'";

    private static string Camel<T>(T value) where T : struct, Enum => SnapshotBuilder.Camel(value);

    internal static Regex GlobToRegex(string glob) =>
        new("^" + Regex.Escape(glob).Replace("\\*", ".*").Replace("\\?", ".") + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    internal static string EncodeCursor(int offset) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"v1:{offset}")).TrimEnd('=');

    internal static int DecodeCursor(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor)) return 0;
        try
        {
            var padded = cursor.PadRight(cursor.Length + (4 - cursor.Length % 4) % 4, '=');
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            if (text.StartsWith("v1:", StringComparison.Ordinal) && int.TryParse(text.AsSpan(3), out var offset) && offset >= 0)
                return offset;
        }
        catch (FormatException)
        {
        }
        throw new ArgumentException("Invalid cursor. Pass the nextCursor value from a previous call unchanged.", nameof(cursor));
    }
}
