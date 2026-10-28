# Diff change kinds and severities

`diff_model`, `tabulartools diff` and `tabulartools review` compare two versions of a model **by meaning**: they match objects
across the versions and compare their semantic properties, never lines of TMDL. Every difference becomes a change record
with a **kind** and a **severity**. This page lists every kind and its default severity. The list is generated from
`SeverityPolicy` in `src/TabularTools.Core/Diff/SeverityPolicy.cs`, and a test checks that the two stay in sync.

## Severities

| Severity | Meaning |
|---|---|
| 🔴 `breaking` | Likely to break consumers or change security: removals, renames, measures moved between tables, RLS and OLS changes. Thin reports and other models bind to objects **by name**, so a rename breaks them just like a removal. |
| 🟠 `high` | Changes the numbers: DAX, relationships (direction, cardinality, active), data types, partition sources, parameter values. |
| 🟡 `medium` | Changes defaults or behaviour in reports: summarize-by, sort-by, hiding objects, new relationships. |
| ⚪ `low` | Cosmetic but visible: format strings, display folders, descriptions, unhiding. |
| ▫️ `info` | No semantic effect: added objects, and expression changes that only touch whitespace or comments. |

## How objects are matched

1. **Same name** under the same parent. Measures match model-wide, so a measure that changes table is reported as `measure.moved`.
   Relationships are matched by their endpoints (`Sales[DateKey] → Date[DateKey]`), not by their GUID names, and follow renamed tables and columns.
2. **Same lineage tag.** Power BI Desktop keeps `lineageTag` across renames, so this gives a rename with confidence `certain`.
3. **Similarity** for what is still unmatched (same type, same table): equal DAX after normalizing whitespace and comments scores 0.95;
   a data column with the same source column and data type scores 0.9; tables compare their columns and partition sources.
   Pairs scoring at least `diff.renameThreshold` (default 0.8) are reported as renames with confidence `high` (≥ 0.9) or `medium`.
4. Everything left over is added or removed. Objects inside an added or removed table are counted on the table rather than listed one by one.

## Formatting-only changes

An expression whose significant tokens are unchanged (only whitespace or comments differ) is a *formatting-only* change. These are
hidden by default and counted in `summary.formattingOnlyHidden`. Pass `--include-formatting` (`includeFormattingOnly: true`) to list them as
`*.expressionFormatting` changes with severity `info`. DAX keywords and function names compare case-insensitively; M is case-sensitive.

## Overriding severities

Set `diff.severity` in `tabulartools.yaml` to change the severity of single kinds, and `diff.failOn` to decide when `diff` exits with code 1:

```yaml
diff:
  failOn: breaking              # breaking | high | medium | low | info | none (default: none)
  renameThreshold: 0.8
  includeFormattingOnly: false
  severity:
    column.summarizeByChanged: low
    role.added: breaking
```

Unknown kinds and severities are rejected when the config is loaded.

## Change kinds
### Model

| Kind | Default severity | Meaning |
|---|---|---|
| `model.compatibilityLevelChanged` | medium | Compatibility level changed. |
| `model.discourageImplicitMeasuresChanged` | medium | Discourage implicit measures switched on or off. |
| `model.defaultModeChanged` | high | Default storage mode changed (import, DirectQuery, ...). |
| `model.cultureChanged` | low | Model culture changed. |
| `perspective.added` | low | Perspective added. |
| `perspective.removed` | low | Perspective removed. |
| `culture.added` | low | Translation culture added. |
| `culture.removed` | low | Translation culture removed. |

### Tables

| Kind | Default severity | Meaning |
|---|---|---|
| `table.added` | info | Table added. |
| `table.removed` | breaking | Table removed; reports and DAX that use it break. |
| `table.renamed` | breaking | Table renamed; reports bind by name. |
| `table.kindChanged` | high | Table changed between regular, calculated and calculation group. |
| `table.expressionChanged` | high | Calculated table DAX changed. |
| `table.expressionFormatting` | info | Calculated table DAX reformatted (whitespace or comments only). |
| `table.hidden` | medium | Table hidden. |
| `table.unhidden` | low | Table unhidden. |
| `table.descriptionChanged` | low | Table description changed. |
| `table.dataCategoryChanged` | low | Table data category changed (e.g. marked as a date table). |
| `calculationGroup.precedenceChanged` | high | Calculation group precedence changed. |

### Columns

| Kind | Default severity | Meaning |
|---|---|---|
| `column.added` | info | Column added. |
| `column.removed` | breaking | Column removed. |
| `column.renamed` | breaking | Column renamed. |
| `column.kindChanged` | high | Column changed between data and calculated. |
| `column.dataTypeChanged` | high | Column data type changed. |
| `column.keyDataTypeChanged` | breaking | Data type changed on a column used by a relationship. |
| `column.expressionChanged` | high | Calculated column DAX changed. |
| `column.expressionFormatting` | info | Calculated column DAX reformatted (whitespace or comments only). |
| `column.sourceColumnChanged` | high | Column now loads from a different source column. |
| `column.summarizeByChanged` | medium | Default summarization changed. |
| `column.sortByChanged` | medium | Sort-by column changed. |
| `column.isKeyChanged` | medium | Key column flag changed. |
| `column.hidden` | medium | Column hidden. |
| `column.unhidden` | low | Column unhidden. |
| `column.formatStringChanged` | low | Column format string changed. |
| `column.displayFolderChanged` | low | Column display folder changed. |
| `column.descriptionChanged` | low | Column description changed. |
| `column.dataCategoryChanged` | low | Column data category changed. |

### Measures

| Kind | Default severity | Meaning |
|---|---|---|
| `measure.added` | info | Measure added. |
| `measure.removed` | breaking | Measure removed. |
| `measure.renamed` | breaking | Measure renamed. |
| `measure.moved` | breaking | Measure moved to another table; report field references include the table. |
| `measure.expressionChanged` | high | Measure DAX changed. |
| `measure.expressionFormatting` | info | Measure DAX reformatted (whitespace or comments only). |
| `measure.formatStringExpressionChanged` | medium | Dynamic format string expression changed. |
| `measure.detailRowsChanged` | medium | Detail rows expression changed. |
| `measure.kpiChanged` | low | KPI added or removed. |
| `measure.hidden` | medium | Measure hidden. |
| `measure.unhidden` | low | Measure unhidden. |
| `measure.formatStringChanged` | low | Measure format string changed. |
| `measure.displayFolderChanged` | low | Measure display folder changed. |
| `measure.descriptionChanged` | low | Measure description changed. |

### Partitions

| Kind | Default severity | Meaning |
|---|---|---|
| `partition.added` | high | Partition added to an existing table. |
| `partition.removed` | high | Partition removed from an existing table. |
| `partition.sourceChanged` | high | Partition source (M, SQL or DAX) changed. |
| `partition.sourceFormatting` | info | Partition source reformatted (whitespace or comments only). |
| `partition.sourceTypeChanged` | high | Partition source type changed. |
| `partition.modeChanged` | high | Partition storage mode changed. |

### Hierarchies

| Kind | Default severity | Meaning |
|---|---|---|
| `hierarchy.added` | info | Hierarchy added. |
| `hierarchy.removed` | breaking | Hierarchy removed. |
| `hierarchy.renamed` | breaking | Hierarchy renamed. |
| `hierarchy.levelsChanged` | medium | Hierarchy levels changed. |
| `hierarchy.hidden` | medium | Hierarchy hidden. |
| `hierarchy.unhidden` | low | Hierarchy unhidden. |
| `hierarchy.displayFolderChanged` | low | Hierarchy display folder changed. |
| `hierarchy.descriptionChanged` | low | Hierarchy description changed. |

### Calculation items

| Kind | Default severity | Meaning |
|---|---|---|
| `calculationItem.added` | info | Calculation item added. |
| `calculationItem.removed` | breaking | Calculation item removed. |
| `calculationItem.renamed` | breaking | Calculation item renamed. |
| `calculationItem.expressionChanged` | high | Calculation item DAX changed. |
| `calculationItem.expressionFormatting` | info | Calculation item DAX reformatted (whitespace or comments only). |
| `calculationItem.ordinalChanged` | high | Calculation item ordinal changed. |
| `calculationItem.formatStringExpressionChanged` | medium | Calculation item format string expression changed. |
| `calculationItem.descriptionChanged` | low | Calculation item description changed. |

### Relationships (matched by endpoints)

| Kind | Default severity | Meaning |
|---|---|---|
| `relationship.added` | medium | Relationship added. |
| `relationship.removed` | high | Relationship removed. |
| `relationship.crossFilterChanged` | high | Cross-filter direction changed. |
| `relationship.cardinalityChanged` | high | Cardinality changed. |
| `relationship.activeChanged` | high | Relationship made active or inactive. |
| `relationship.securityFilteringChanged` | high | Security filtering behaviour changed. |
| `relationship.referentialIntegrityChanged` | medium | 'Assume referential integrity' changed (DirectQuery joins). |

### Roles and security

| Kind | Default severity | Meaning |
|---|---|---|
| `role.added` | high | Role added. |
| `role.removed` | breaking | Role removed. |
| `role.modelPermissionChanged` | breaking | Role model permission changed. |
| `role.rlsFilterAdded` | breaking | Row-level security filter added. |
| `role.rlsFilterChanged` | breaking | Row-level security filter changed. |
| `role.rlsFilterFormatting` | info | Row-level security filter reformatted (whitespace or comments only). |
| `role.rlsFilterRemoved` | breaking | Row-level security filter removed. |
| `role.olsChanged` | breaking | Object-level security (table or column permission) changed. |
| `role.membersChanged` | high | Number of role members changed. |
| `role.descriptionChanged` | low | Role description changed. |

### Shared expressions and parameters

| Kind | Default severity | Meaning |
|---|---|---|
| `expression.added` | info | Shared expression or parameter added. |
| `expression.removed` | high | Shared expression or parameter removed; partitions that use it break. |
| `expression.renamed` | high | Shared expression or parameter renamed. |
| `expression.expressionChanged` | high | Shared expression or parameter value changed. |
| `expression.expressionFormatting` | info | Shared expression reformatted (whitespace or comments only). |
| `expression.kindChanged` | high | Shared expression changed between M and DAX. |
| `expression.descriptionChanged` | low | Shared expression description changed. |

### DAX user-defined functions

| Kind | Default severity | Meaning |
|---|---|---|
| `function.added` | info | DAX function added. |
| `function.removed` | breaking | DAX function removed. |
| `function.renamed` | breaking | DAX function renamed. |
| `function.expressionChanged` | high | DAX function body changed. |
| `function.expressionFormatting` | info | DAX function reformatted (whitespace or comments only). |
| `function.hidden` | medium | DAX function hidden. |
| `function.unhidden` | low | DAX function unhidden. |
| `function.descriptionChanged` | low | DAX function description changed. |

## Not compared yet

Annotations, translations (per-object captions), perspective membership, KPI details beyond added/removed, and relationship
properties other than those listed are not compared yet. Changes there don't appear in the diff.
