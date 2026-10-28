<!-- tabulartools:diff -->
### Semantic model review — `ExampleSales` · base `{main}` → head `{feature}`

🔴 5 breaking · 🟠 4 high · 🟡 2 medium · ⚪ 1 low · ▫️ 1 info

#### 🔴 Breaking (5)

| Object | Change | Impact |
|---|---|---|
| `role West Region · Store` | RLS filter changed `[Region] = "West"` → `[Region] IN { "West", "North" }` | security |
| `Customer[Customer Name]` → `Customer[Customer Full Name]` | column renamed (high: same source column) | — |
| `Sales[Customer Count]` → `Customer[Customer Count]` | measure moved from table `Sales` to `Customer` | — |
| `Sales[Average Price]` | measure removed | — |
| `Sales[Total Quantity]` → `Sales[Units Sold]` | measure renamed (certain: lineage tag) | — |

<details><summary>🟠 High (4)</summary>

| Object | Change | Impact |
|---|---|---|
| `Sales[ProductKey] → Product[ProductKey]` | cross-filter direction changed `oneDirection` → `bothDirections` | — |
| `Sales[StoreKey] → Store[StoreKey]` | cardinality changed `many-to-one` → `many-to-many` | — |
| `Product[List Price]` | data type changed `decimal` → `double` | — |
| `Sales[Total Sales]` | DAX changed `SUM(Sales[Amount])` → `SUMX(Sales, Sales[Quantity] * Sales[Unit Price])` | 5 downstream: `Sales[Margin]`, `Sales[Margin %]`, `Sales[Sales YTD]` +2 more |

</details>

<details><summary>🟡 Medium (2)</summary>

| Object | Change | Impact |
|---|---|---|
| `Sales[CustomerKey]` | column hidden | 2 downstream: `Customer[Customer Count]`, `Sales[CustomerKey] → Customer[CustomerKey]` |
| `Sales[Quantity]` | summarize by changed `sum` → `none` | 8 downstream: `Sales[Total Sales]`, `Sales[Units Sold]`, `Sales[Total Cost]` +5 more |

</details>

<details><summary>⚪ Low (1)</summary>

| Object | Change | Impact |
|---|---|---|
| `Sales[Sales PY]` | display folder changed `Time` → `Time Intelligence` | 1 downstream: `Sales[Sales YoY %]` |

</details>

<details><summary>▫️ Info (1)</summary>

| Object | Change | Impact |
|---|---|---|
| `Promotion` | table added (2 columns, 1 measure) | — |

</details>

<details><summary>Expression diffs (2)</summary>

**role West Region · Store** — RLS filter changed · `ExampleSales.SemanticModel/definition/roles/West Region.tmdl:4`

```diff
@@ -1 +1 @@
-[Region] = "West"
+[Region] IN { "West", "North" }
```

**Sales[Total Sales]** — DAX changed · `ExampleSales.SemanticModel/definition/tables/Sales.tmdl:6`

```diff
@@ -1 +1 @@
-SUM(Sales[Amount])
+SUMX(Sales, Sales[Quantity] * Sales[Unit Price])
```

</details>

_1 formatting-only expression change hidden._


<sub>{product}</sub>
