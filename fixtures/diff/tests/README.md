# Measure test fixture for the v1 → v2 diff

Tests copy these files into the temporary git repository built from `ExampleSales-v1` (`main`) and `ExampleSales-v2`
(`feature`), then run the differential tests with base `main`. The recordings are hand-written synthetic values; the
queries in them are the planner's output (`UPDATE_GOLDEN=1` rewrites the query text, keeping the values).

Expected outcome: `sales-by-year` fails (`[Total Sales]` was edited, so its change is expected, but `[Margin %]` changes
too without being edited), `total-sales-only` is an expected change, and the renamed `[Units Sold]` and moved
`[Customer Count]` pass.
