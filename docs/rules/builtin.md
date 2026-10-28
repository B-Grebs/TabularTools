# Built-in lint rules

These rules ship with TabularTools (ruleset `builtin`). They cover checks that are hard to express as Best Practice Analyzer expressions because they need the DAX dependency graph or the relationship graph. Change a rule's severity, or turn it off, in `tabulartools.yaml`:

```yaml
lint:
  severity:
    TT004: off
    TT005: error
```

<a id="tt001"></a>
## TT001 — DAX expression references an object that doesn't exist

**Severity:** error · **Applies to:** measures (expression, format string, detail rows), calculated columns and tables, calculation items, RLS filters, DAX functions

A qualified reference (`Sales[Qty]`) names a column or measure that isn't in that table, or a table that doesn't exist. Or an unqualified `[Name]` matches no measure, no column, and no column defined inside the same expression (such as an `ADDCOLUMNS` name). The expression fails when it's evaluated.

**Fix:** correct the reference, or restore the object that was renamed or removed. `get_dependencies` / `tabulartools deps` lists the unresolved references of any object.

<a id="tt002"></a>
## TT002 — Bidirectional relationship creates ambiguous filter paths

**Severity:** warning · **Applies to:** relationships

Filters flow from the one side to the many side of each active relationship, and both ways for bidirectional, one-to-one and many-to-many relationships. When a bidirectional relationship gives a filter two or more routes to the same table, results depend on which route the engine takes.

**Fix:** make the relationship single-direction, and use `CROSSFILTER(..., Both)` inside the measures that need two-way filtering.

<a id="tt003"></a>
## TT003 — RLS filter uses a column from another table without qualifying it

**Severity:** error · **Applies to:** role table permissions

An unqualified `[Column]` in a role's table filter is evaluated in the row context of the secured table. If that column lives in a different table, the filter doesn't do what it looks like it does.

**Fix:** qualify the reference (`Customer[Segment]`) with a function that crosses tables, such as `LOOKUPVALUE` or `RELATED`, or move the filter to the table that owns the column.

<a id="tt004"></a>
## TT004 — Data source server or database is hard-coded

**Severity:** info · **Applies to:** M partitions and shared expressions (except M parameters)

A connector such as `Sql.Database("server", "db")` gets literal names. The same model can't then point at dev, test and production without editing every partition.

**Fix:** create M parameters (for example `ServerName`, `DatabaseName`) and pass those to the connector.

<a id="tt005"></a>
## TT005 — Measure has the same name as a column

**Severity:** warning · **Applies to:** measures

`[Name]` then means a measure in one place and a column in another, which makes DAX hard to read and easy to break during refactoring.

**Fix:** rename the measure (often with a verb or aggregation, such as `Total Quantity`) or the column.
