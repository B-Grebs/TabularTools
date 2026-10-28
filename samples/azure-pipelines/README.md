# Azure Pipelines samples

| File | What it does |
|---|---|
| [`pr-review.yml`](pr-review.yml) | Pull request validation for repositories with PBIP/TMDL semantic models. Runs `tabulartools review` (semantic diff of every changed model, lint violations the PR introduces, and measure tests when a service connection is set), publishes the report as a pipeline artifact, posts it as one PR comment that is updated on every push, and fails the run on blocking findings. |

The setup steps (branch policy, the build service's *Contribute to pull requests* permission, the package feed) are at the top of the file.

## Adjusting what blocks a PR

Put a `tabulartools.yaml` at the repository root:

```yaml
lint:
  failOn: error        # new lint violations at or above this level block the PR
diff:
  failOn: breaking     # semantic changes at or above this level block the PR (default: none)
  severity:
    column.summarizeByChanged: low
```

See [docs/diff-severities.md](../../docs/diff-severities.md) for every change kind.

## Measure tests in the pipeline

`review` also runs the measure tests listed under `tests.files`, comparing the PR's measure definitions with the target
branch's on a deployed dev model (read-only, nothing is deployed):

```yaml
profiles:
  dev:
    endpoint: powerbi://api.powerbi.com/v1.0/myorg/Sales%20Dev
    database: Sales
    auth: azureCli       # uses the AzureCLI@2 task's sign-in
tests:
  files: ["tests/**/*.measuretests.yaml"]
  profile: dev
```

Set the `SERVICE_CONNECTION` variable to an Azure Resource Manager service connection that uses workload identity
federation. Its identity (a service principal) needs Build permission on the dev model, and the tenant must allow
service principals to use the Power BI APIs. With the variable empty the review runs without tests. To run tests without
a live model at all, point a profile at a recordings file (`recordings: tests/sales.recordings.yaml`) captured with
`tabulartools test run --record`.

## Notes

- `fetchDepth: 0` is required. With a shallow clone the base branch or the merge base is missing, and `review` exits with code 4 and says so.
- The comment is found again by its marker (`<!-- tabulartools:review -->`), so pushes update the same comment instead of adding new ones.
  Use a different `--marker` per pipeline if several post comments.
- Comments longer than about 140,000 characters are cut, with a link to the `tabulartools-review` artifact that holds the full report.
- **Measure tests against the service aren't verified yet** (no tenant in the build environment); `scripts/verify-live.ps1` checks them.
- **Not yet verified against a live Azure DevOps organization.** The REST calls follow the documented API (version 7.1) and are covered by
  tests against a fake server.
