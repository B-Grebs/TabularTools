using TabularTools.Core.Dax;
using TabularTools.Core.Snapshots;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace TabularTools.Core.Lint.Compat;

// Read-only facade over TOM that exposes Tabular Editor's member names, so Best Practice Analyzer rule
// expressions (Dynamic LINQ over Tabular Editor's object model) can be evaluated unchanged.
// Semantics follow Tabular Editor 2's TOMWrapper (MIT, Copyright (c) Tabular Editor ApS); see THIRD_PARTY_NOTICES.md.
// Enum-valued properties are exposed as strings ("Double", "BothDirections"); the dialect adapter rewrites
// "DataType.Double"-style literals to match. Nothing here can modify the model.

#pragma warning disable CA1716, CA1720, CA1721 // Member names mirror Tabular Editor's object model.

public abstract class TeObject
{
    private protected TeObject(TeModel? model, string key, string objectType, object? tom)
    {
        ModelInternal = model;
        Key = key;
        ObjectType = objectType;
        Tom = tom;
    }

    internal TeModel? ModelInternal { get; set; }

    internal object? Tom { get; }

    /// <summary>Stable object key (see <see cref="ObjectKeys"/>).</summary>
    public string Key { get; }

    public abstract string Name { get; }

    /// <summary>Tabular Editor object type: Model, Table, Column, Measure, Hierarchy, Level, Partition, Relationship, ...</summary>
    public string ObjectType { get; }

    public virtual string ObjectTypeName => ObjectType;

    public virtual string Description => "";

    public TeModel Model => ModelInternal ?? (TeModel)this;

    public virtual string? DaxObjectName => null;

    public string? GetAnnotation(string name) =>
        Annotations().FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.Ordinal))?.Value;

    public bool HasAnnotation(string name) => GetAnnotation(name) is not null;

    internal IEnumerable<TOM.Annotation> Annotations() =>
        Tom?.GetType().GetProperty("Annotations")?.GetValue(Tom) is IEnumerable<TOM.Annotation> annotations ? annotations : [];

    public override string ToString() => Name;
}

/// <summary>Objects with DAX expressions.</summary>
public interface ITeDaxObject
{
    TeDependsOn DependsOn { get; }
}

public sealed class TeModel : TeObject
{
    private readonly Dictionary<string, TeObject> _registry = new(StringComparer.Ordinal);
    private readonly TOM.Model _model;

    internal TeModel(TOM.Database database, DependencyGraph graph) : base(null, ObjectKeys.Model, "Model", database.Model)
    {
        _model = database.Model;
        Graph = graph;
        Register(this);

        Tables = _model.Tables.Select(t => new TeTable(this, t)).ToList();
        DataSources = _model.DataSources.Select(d => new TeDataSource(this, d)).ToList();
        Relationships = _model.Relationships.OfType<TOM.SingleColumnRelationship>().Select(r => new TeRelationship(this, r)).ToList();
        Roles = _model.Roles.Select(r => new TeModelRole(this, r)).ToList();
        Perspectives = _model.Perspectives.Select(p => new TePerspective(this, p)).ToList();
        Cultures = _model.Cultures.Select(c => new TeCulture(this, c)).ToList();
        Expressions = _model.Expressions.Select(e => new TeNamedExpression(this, e)).ToList();
        Functions = _model.Functions.Select(f => new TeFunction(this, f)).ToList();
    }

    internal DependencyGraph Graph { get; }

    internal void Register(TeObject o) => _registry[o.Key] = o;

    internal TeObject? Find(string key) => _registry.TryGetValue(key, out var o) ? o : null;

    internal T? Find<T>(string key) where T : TeObject => Find(key) as T;

    internal IEnumerable<TeObject> AllObjects => _registry.Values;

    public override string Name => _model.Name;
    public override string Description => _model.Description ?? "";
    public List<TeTable> Tables { get; }
    public List<TeRelationship> Relationships { get; }
    public List<TeModelRole> Roles { get; }
    public List<TePerspective> Perspectives { get; }
    public List<TeCulture> Cultures { get; }
    public List<TeDataSource> DataSources { get; }
    public List<TeNamedExpression> Expressions { get; }
    public List<TeFunction> Functions { get; }
    public List<TeColumn> AllColumns => Tables.SelectMany(t => t.Columns).ToList();
    public List<TeMeasure> AllMeasures => Tables.SelectMany(t => t.Measures).ToList();
    public List<TeHierarchy> AllHierarchies => Tables.SelectMany(t => t.Hierarchies).ToList();
    public List<TeLevel> AllLevels => AllHierarchies.SelectMany(h => h.Levels).ToList();
    public List<TePartition> AllPartitions => Tables.SelectMany(t => t.Partitions).ToList();
    public List<TeCalculationItem> AllCalculationItems => Tables.SelectMany(t => t.CalculationItems).ToList();
    public List<TeTable> CalculationGroups => Tables.Where(t => t.IsCalculationGroup).ToList();
    public string DefaultPowerBIDataSourceVersion => _model.DefaultPowerBIDataSourceVersion.ToString();
    public string DefaultMode => _model.DefaultMode.ToString();
    public string Culture => _model.Culture ?? "";
    public bool DiscourageImplicitMeasures => _model.DiscourageImplicitMeasures;
    public int CompatibilityLevel => _model.Database?.CompatibilityLevel ?? 0;
}

public sealed class TeTable : TeObject, ITeDaxObject
{
    private readonly TOM.Table _t;

    internal TeTable(TeModel model, TOM.Table t) : base(model, ObjectKeys.Table(t.Name), "Table", t)
    {
        _t = t;
        model.Register(this);
        Columns = t.Columns.Where(c => c.Type != TOM.ColumnType.RowNumber).Select(c => new TeColumn(model, this, c)).ToList();
        Measures = t.Measures.Select(m => new TeMeasure(model, this, m)).ToList();
        Hierarchies = t.Hierarchies.Select(h => new TeHierarchy(model, this, h)).ToList();
        Partitions = t.Partitions.Select(p => new TePartition(model, this, p)).ToList();
        CalculationItems = t.CalculationGroup?.CalculationItems.Select(ci => new TeCalculationItem(model, this, ci)).ToList() ?? [];
    }

    public override string Name => _t.Name;
    public override string Description => _t.Description ?? "";
    public override string DaxObjectName => $"'{_t.Name.Replace("'", "''")}'";
    public override string ObjectTypeName =>
        IsCalculationGroup ? "Calculation Group Table"
        : IsCalculatedTable ? "Calculated Table"
        : _t.Partitions.Any(p => p.Mode == TOM.ModeType.DirectQuery) ? "Table (DirectQuery)"
        : "Table";

    internal bool IsCalculationGroup => _t.CalculationGroup is not null;
    internal bool IsCalculatedTable => !IsCalculationGroup && _t.Partitions.Any(p => p.SourceType == TOM.PartitionSourceType.Calculated);

    public TeTable Table => this;
    public List<TeColumn> Columns { get; }
    public List<TeMeasure> Measures { get; }
    public List<TeHierarchy> Hierarchies { get; }
    public List<TePartition> Partitions { get; }
    public List<TeCalculationItem> CalculationItems { get; }
    public bool IsHidden => _t.IsHidden;
    public bool IsVisible => !_t.IsHidden;
    public bool IsPrivate => _t.IsPrivate;
    public string DataCategory => _t.DataCategory ?? "";
    public string LineageTag => _t.LineageTag ?? "";
    public int Precedence => _t.CalculationGroup?.Precedence ?? 0;
    public string Expression => _t.Partitions.Select(p => p.Source).OfType<TOM.CalculatedPartitionSource>().FirstOrDefault()?.Expression ?? "";
    public string SourceExpression => string.Join(Environment.NewLine, Partitions.Select(p => p.Query ?? ""));
    public List<TeRelationship> UsedInRelationships => Model.Relationships.Where(r => r.FromTable == this || r.ToTable == this).ToList();
    public TeRlsIndexer RowLevelSecurity => new(Model.Roles.SelectMany(r => r.TablePermissions.Where(tp => tp.Table == this).Select(tp => (r.Name, tp.Expression))));
    public TeNameIndexer<string> ObjectLevelSecurity => new(Model.Roles.SelectMany(r => r.TablePermissions.Where(tp => tp.Table == this).Select(tp => (r.Name, tp.MetadataPermission))));
    public TePerspectiveIndexer InPerspective => new(Model, _t);
    public List<TeToken> Tokenize() => TeToken.From(Expression);
    public TeDependsOn DependsOn => TeDependsOn.For(Model, Key);
    public TeReferencedBy ReferencedBy => TeReferencedBy.For(Model, Key);
}

public sealed class TeColumn : TeObject, ITeDaxObject
{
    private readonly TOM.Column _c;

    internal TeColumn(TeModel model, TeTable table, TOM.Column c) : base(model, ObjectKeys.Column(table.Name, c.Name), "Column", c)
    {
        _c = c;
        Table = table;
        model.Register(this);
    }

    public override string Name => _c.Name;
    public override string Description => _c.Description ?? "";
    public override string DaxObjectName => $"{Table.DaxObjectName}[{_c.Name.Replace("]", "]]")}]";
    public override string ObjectTypeName => _c.Type switch
    {
        TOM.ColumnType.Calculated => "Calculated Column",
        TOM.ColumnType.CalculatedTableColumn => "Calculated Table Column",
        _ => "Data Column",
    };

    public TeTable Table { get; }
    public string DataType => _c.DataType.ToString();
    public string Type => _c.Type.ToString();
    public bool IsHidden => _c.IsHidden;
    public bool IsVisible => !_c.IsHidden;
    public bool IsKey => _c.IsKey;
    public bool IsUnique => _c.IsUnique;
    public bool IsNullable => _c.IsNullable;
    public bool IsAvailableInMDX => _c.IsAvailableInMDX;
    public string SummarizeBy => _c.SummarizeBy.ToString();
    public string FormatString => _c.FormatString ?? "";
    public string DataCategory => _c.DataCategory ?? "";
    public string DisplayFolder => _c.DisplayFolder ?? "";
    public string LineageTag => _c.LineageTag ?? "";
    public string Expression => (_c as TOM.CalculatedColumn)?.Expression ?? "";
    public string SourceColumn => (_c switch { TOM.DataColumn d => d.SourceColumn, TOM.CalculatedTableColumn ct => ct.SourceColumn, _ => null }) ?? "";
    public object? AlternateOf => _c.AlternateOf;
    public TeColumn? SortByColumn => _c.SortByColumn is { } s ? Model.Find<TeColumn>(ObjectKeys.Column(Table.Name, s.Name)) : null;
    public List<TeRelationship> UsedInRelationships => Model.Relationships.Where(r => r.FromColumn == this || r.ToColumn == this).ToList();
    public List<TeColumn> UsedInSortBy => Table.Columns.Where(c => c.SortByColumn == this).ToList();
    public List<TeHierarchy> UsedInHierarchies => Table.Hierarchies.Where(h => h.Levels.Any(l => l.Column == this)).ToList();
    public List<object> UsedInVariations => _c.Table.Model.Tables.SelectMany(t => t.Columns).SelectMany(c => c.Variations)
        .Where(v => v.DefaultColumn == _c || v.Relationship is TOM.SingleColumnRelationship r && (r.FromColumn == _c || r.ToColumn == _c)).Cast<object>().ToList();
    public TeNameIndexer<string> ObjectLevelSecurity => new(Model.Roles.SelectMany(r => r.TablePermissions.Where(tp => tp.Table == Table)
        .SelectMany(tp => tp.ColumnPermissions.Where(cp => cp.Column == Name).Select(cp => (r.Name, cp.MetadataPermission)))));
    public List<TeToken> Tokenize() => TeToken.From(Expression);
    public TeDependsOn DependsOn => TeDependsOn.For(Model, Key);
    public TeReferencedBy ReferencedBy => TeReferencedBy.For(Model, Key);
}

public sealed class TeMeasure : TeObject, ITeDaxObject
{
    private readonly TOM.Measure _m;

    internal TeMeasure(TeModel model, TeTable table, TOM.Measure m) : base(model, ObjectKeys.Measure(table.Name, m.Name), "Measure", m)
    {
        _m = m;
        Table = table;
        model.Register(this);
        KPI = m.KPI is null ? null : new TeKpi(model, this, m.KPI);
    }

    public override string Name => _m.Name;
    public override string Description => _m.Description ?? "";
    public override string DaxObjectName => $"[{_m.Name.Replace("]", "]]")}]";
    public TeTable Table { get; }
    public string Expression => _m.Expression ?? "";
    public string FormatString => _m.FormatString ?? "";
    public string FormatStringExpression => _m.FormatStringDefinition?.Expression ?? "";
    public string DetailRowsExpression => _m.DetailRowsDefinition?.Expression ?? "";
    public string DisplayFolder => _m.DisplayFolder ?? "";
    public string DataCategory => _m.DataCategory ?? "";
    public string LineageTag => _m.LineageTag ?? "";
    public string DataType => _m.DataType.ToString();
    public bool IsHidden => _m.IsHidden;
    public bool IsVisible => !_m.IsHidden;
    public TeKpi? KPI { get; }
    public List<TeToken> Tokenize() => TeToken.From(Expression);
    public TeDependsOn DependsOn => TeDependsOn.For(Model, Key);
    public TeReferencedBy ReferencedBy => TeReferencedBy.For(Model, Key);
}

public sealed class TeKpi : TeObject
{
    private readonly TOM.KPI _k;

    internal TeKpi(TeModel model, TeMeasure measure, TOM.KPI k) : base(model, ObjectKeys.Kpi(measure.Table.Name, measure.Name), "KPI", k)
    {
        _k = k;
        Measure = measure;
        model.Register(this);
    }

    public override string Name => Measure.Name;
    public override string Description => _k.Description ?? "";
    public TeMeasure Measure { get; }
    public TeTable Table => Measure.Table;
    public string TargetExpression => _k.TargetExpression ?? "";
    public string StatusExpression => _k.StatusExpression ?? "";
    public string TrendExpression => _k.TrendExpression ?? "";
    public string Expression => string.Join(Environment.NewLine, new[] { TargetExpression, StatusExpression, TrendExpression }.Where(e => !string.IsNullOrEmpty(e)));
    /// <summary>KPI expressions aren't analysed for dependencies yet.</summary>
    public TeDependsOn DependsOn => TeDependsOn.Empty;
}

public sealed class TeHierarchy : TeObject
{
    private readonly TOM.Hierarchy _h;

    internal TeHierarchy(TeModel model, TeTable table, TOM.Hierarchy h) : base(model, ObjectKeys.Hierarchy(table.Name, h.Name), "Hierarchy", h)
    {
        _h = h;
        Table = table;
        model.Register(this);
        Levels = h.Levels.OrderBy(l => l.Ordinal).Select(l => new TeLevel(model, this, l)).ToList();
    }

    public override string Name => _h.Name;
    public override string Description => _h.Description ?? "";
    public TeTable Table { get; }
    public List<TeLevel> Levels { get; }
    public bool IsHidden => _h.IsHidden;
    public bool IsVisible => !_h.IsHidden;
    public string DisplayFolder => _h.DisplayFolder ?? "";
}

public sealed class TeLevel : TeObject
{
    private readonly TOM.Level _l;

    internal TeLevel(TeModel model, TeHierarchy hierarchy, TOM.Level l)
        : base(model, ObjectKeys.Level(hierarchy.Table.Name, hierarchy.Name, l.Name), "Level", l)
    {
        _l = l;
        Hierarchy = hierarchy;
        model.Register(this);
    }

    public override string Name => _l.Name;
    public override string Description => _l.Description ?? "";
    public TeHierarchy Hierarchy { get; }
    public TeTable Table => Hierarchy.Table;
    public int Ordinal => _l.Ordinal;
    public TeColumn? Column => Model.Find<TeColumn>(ObjectKeys.Column(Table.Name, _l.Column.Name));
}

public sealed class TePartition : TeObject
{
    private readonly TOM.Partition _p;

    internal TePartition(TeModel model, TeTable table, TOM.Partition p) : base(model, ObjectKeys.Partition(table.Name, p.Name), "Partition", p)
    {
        _p = p;
        Table = table;
        model.Register(this);
    }

    public override string Name => _p.Name;
    public override string Description => _p.Description ?? "";
    public TeTable Table { get; }
    public string SourceType => _p.SourceType.ToString();
    public string Mode => _p.Mode.ToString();
    public string Query => (_p.Source switch
    {
        TOM.MPartitionSource m => m.Expression,
        TOM.QueryPartitionSource q => q.Query,
        TOM.CalculatedPartitionSource c => c.Expression,
        TOM.EntityPartitionSource e => e.EntityName,
        _ => null,
    }) ?? "";
    public string Expression => Query ?? "";
    public TeDataSource? DataSource => _p.Source switch
    {
        TOM.QueryPartitionSource { DataSource: { } ds } => Model.Find<TeDataSource>(ObjectKeys.DataSource(ds.Name)),
        TOM.EntityPartitionSource { DataSource: { } ds } => Model.Find<TeDataSource>(ObjectKeys.DataSource(ds.Name)),
        _ => null,
    };
}

public sealed class TeRelationship : TeObject
{
    private readonly TOM.SingleColumnRelationship _r;

    internal TeRelationship(TeModel model, TOM.SingleColumnRelationship r)
        : base(model, ObjectKeys.Relationship(r.FromTable.Name, r.FromColumn.Name, r.ToTable.Name, r.ToColumn.Name), "Relationship", r)
    {
        _r = r;
        model.Register(this);
    }

    public override string Name => _r.Name;
    public TeTable FromTable => Model.Find<TeTable>(ObjectKeys.Table(_r.FromTable.Name))!;
    public TeTable ToTable => Model.Find<TeTable>(ObjectKeys.Table(_r.ToTable.Name))!;
    public TeColumn FromColumn => Model.Find<TeColumn>(ObjectKeys.Column(_r.FromTable.Name, _r.FromColumn.Name))!;
    public TeColumn ToColumn => Model.Find<TeColumn>(ObjectKeys.Column(_r.ToTable.Name, _r.ToColumn.Name))!;
    public string FromCardinality => _r.FromCardinality.ToString();
    public string ToCardinality => _r.ToCardinality.ToString();
    public string CrossFilteringBehavior => _r.CrossFilteringBehavior.ToString();
    public string SecurityFilteringBehavior => _r.SecurityFilteringBehavior.ToString();
    public string JoinOnDateBehavior => _r.JoinOnDateBehavior.ToString();
    public bool IsActive => _r.IsActive;
    public bool RelyOnReferentialIntegrity => _r.RelyOnReferentialIntegrity;
}

public sealed class TeModelRole : TeObject
{
    private readonly TOM.ModelRole _r;

    internal TeModelRole(TeModel model, TOM.ModelRole r) : base(model, ObjectKeys.Role(r.Name), "ModelRole", r)
    {
        _r = r;
        model.Register(this);
        TablePermissions = r.TablePermissions.Select(tp => new TeTablePermission(model, this, tp)).ToList();
    }

    public override string Name => _r.Name;
    public override string Description => _r.Description ?? "";
    public string ModelPermission => _r.ModelPermission.ToString();
    public List<string> Members => _r.Members.Select(m => m.MemberName).ToList();
    public List<TeTablePermission> TablePermissions { get; }
    public TeRlsIndexer RowLevelSecurity => new(TablePermissions.Select(tp => (tp.Table.Name, tp.Expression)));
}

public sealed class TeTablePermission : TeObject, ITeDaxObject
{
    private readonly TOM.TablePermission _tp;

    internal TeTablePermission(TeModel model, TeModelRole role, TOM.TablePermission tp)
        : base(model, ObjectKeys.TablePermission(role.Name, tp.Table.Name), "TablePermission", tp)
    {
        _tp = tp;
        Role = role;
        model.Register(this);
    }

    public override string Name => _tp.Table.Name;
    public TeModelRole Role { get; }
    public TeTable Table => Model.Find<TeTable>(ObjectKeys.Table(_tp.Table.Name))!;
    public string Expression => _tp.FilterExpression ?? "";
    public string FilterExpression => Expression ?? "";
    public string MetadataPermission => _tp.MetadataPermission.ToString();
    internal IEnumerable<(string Column, string MetadataPermission)> ColumnPermissions =>
        _tp.ColumnPermissions.Select(cp => (cp.Column.Name, cp.MetadataPermission.ToString()));
    public List<TeToken> Tokenize() => TeToken.From(Expression);
    public TeDependsOn DependsOn => TeDependsOn.For(Model, Key);
}

public sealed class TeCalculationItem : TeObject, ITeDaxObject
{
    private readonly TOM.CalculationItem _ci;

    internal TeCalculationItem(TeModel model, TeTable table, TOM.CalculationItem ci)
        : base(model, ObjectKeys.CalculationItem(table.Name, ci.Name), "CalculationItem", ci)
    {
        _ci = ci;
        CalculationGroup = table;
        model.Register(this);
    }

    public override string Name => _ci.Name;
    public override string Description => _ci.Description ?? "";
    public TeTable CalculationGroup { get; }
    public TeTable Table => CalculationGroup;
    public string Expression => _ci.Expression ?? "";
    public string FormatStringExpression => _ci.FormatStringDefinition?.Expression ?? "";
    public int Ordinal => _ci.Ordinal;
    public List<TeToken> Tokenize() => TeToken.From(Expression);
    public TeDependsOn DependsOn => TeDependsOn.For(Model, Key);
}

public sealed class TePerspective : TeObject
{
    private readonly TOM.Perspective _p;

    internal TePerspective(TeModel model, TOM.Perspective p) : base(model, ObjectKeys.Perspective(p.Name), "Perspective", p)
    {
        _p = p;
        model.Register(this);
    }

    public override string Name => _p.Name;
    public override string Description => _p.Description ?? "";
}

public sealed class TeCulture : TeObject
{
    private readonly TOM.Culture _c;

    internal TeCulture(TeModel model, TOM.Culture c) : base(model, ObjectKeys.Culture(c.Name), "Culture", c)
    {
        _c = c;
        model.Register(this);
    }

    public override string Name => _c.Name;
}

public sealed class TeDataSource : TeObject
{
    private readonly TOM.DataSource _d;

    internal TeDataSource(TeModel model, TOM.DataSource d)
        : base(model, ObjectKeys.DataSource(d.Name), d.Type == TOM.DataSourceType.Structured ? "StructuredDataSource" : "ProviderDataSource", d)
    {
        _d = d;
        model.Register(this);
    }

    public override string Name => _d.Name;
    public override string Description => _d.Description ?? "";
    public string Type => _d.Type.ToString();
    public string ConnectionString => (_d as TOM.ProviderDataSource)?.ConnectionString ?? "";
    public List<TePartition> UsedByPartitions => Model.AllPartitions.Where(p => p.DataSource == this).ToList();
}

public sealed class TeNamedExpression : TeObject
{
    private readonly TOM.NamedExpression _e;

    internal TeNamedExpression(TeModel model, TOM.NamedExpression e) : base(model, ObjectKeys.Expression(e.Name), "NamedExpression", e)
    {
        _e = e;
        model.Register(this);
    }

    public override string Name => _e.Name;
    public override string Description => _e.Description ?? "";
    public string Expression => _e.Expression ?? "";
    public string Kind => _e.Kind.ToString();
}

public sealed class TeFunction : TeObject, ITeDaxObject
{
    private readonly TOM.Function _f;

    internal TeFunction(TeModel model, TOM.Function f) : base(model, ObjectKeys.Function(f.Name), "Function", f)
    {
        _f = f;
        model.Register(this);
    }

    public override string Name => _f.Name;
    public override string Description => _f.Description ?? "";
    public string Expression => _f.Expression ?? "";
    public bool IsHidden => _f.IsHidden;
    public List<TeToken> Tokenize() => TeToken.From(Expression);
    public TeDependsOn DependsOn => TeDependsOn.For(Model, Key);
    public TeReferencedBy ReferencedBy => TeReferencedBy.For(Model, Key);
}

#pragma warning restore CA1716, CA1720, CA1721
