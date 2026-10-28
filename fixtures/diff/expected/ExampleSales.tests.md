<!-- tabulartools:measure-tests -->
### ❌ Measure tests · base `{main}` → head `working tree`

2 passed, 1 failed, 1 changed as expected. Evaluated on `ExampleSales` with query-scoped overrides; nothing was deployed.

**`sales.measuretests.yaml`** — 2 passed, 1 failed, 1 changed as expected
Edited measures: `'Sales'[Total Sales]`

| | Case | Assertion | Result |
|---|---|---|---|
| ❌ | `sales-by-year` | differential | Changed without being edited: [Margin %] (2 rows). |
| 🔵 | `total-sales-only` | differential | Expected change in edited measure(s): [Total Sales] (2 rows). |

<details open><summary>❌ <code>sales-by-year</code>: 4 values differ</summary>

| Row | Measure | Base | Head | Δ |
|---|---|---:|---:|---:|
| Date[Year] = 2023 | ⚠️ Margin % | 0.312 | 0.3155 | +0.0035 |
| Date[Year] = 2024 | ⚠️ Margin % | 0.318 | 0.3229 | +0.0049 |
| Date[Year] = 2023 | Total Sales | 1234567.00 | 1240100.00 | +5533 |
| Date[Year] = 2024 | Total Sales | 1321200.15 | 1330000.00 | +8799.85 |

</details>

<details><summary>🔵 <code>total-sales-only</code>: 2 values differ</summary>

| Row | Measure | Base | Head | Δ |
|---|---|---:|---:|---:|
| Date[Year] = 2023 | Total Sales | 523456.25 | 525800.00 | +2343.75 |
| Date[Year] = 2024 | Total Sales | 560330.40 | 563900.10 | +3569.7 |

</details>

<details><summary>⚠️ 5 structural changes not covered by these tests</summary>

- role West Region · Store: RLS filter changed
- Customer[Customer Full Name]: renamed
- Sales[ProductKey] → Product[ProductKey]: cross-filter direction changed
- Sales[StoreKey] → Store[StoreKey]: cardinality changed
- Product[List Price]: data type changed

Queries can't simulate relationship, data type, partition, RLS or calculated table changes; test them on a deployed copy.

</details>

<sub>{product}</sub>
