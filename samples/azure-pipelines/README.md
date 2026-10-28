# Azure Pipelines samples

| File | What it does |
|---|---|
| [`pr-review.yml`](pr-review.yml) | Pull request validation for repositories with PBIP/TMDL semantic models. Runs `tabulartools review` (semantic diff of every changed model plus lint violations the PR introduces), publishes the report as a pipeline artifact, posts it as one PR comment that is updated on every push, and fails the run on blocking findings. |

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

## Notes

- `fetchDepth: 0` is required. With a shallow clone the base branch or the merge base is missing, and `review` exits with code 4 and says so.
- The comment is found again by its marker (`<!-- tabulartools:review -->`), so pushes update the same comment instead of adding new ones.
  Use a different `--marker` per pipeline if several post comments.
- Comments longer than about 140,000 characters are cut, with a link to the `tabulartools-review` artifact that holds the full report.
- **Not yet verified against a live Azure DevOps organization.** The REST calls follow the documented API (version 7.1) and are covered by
  tests against a fake server.
