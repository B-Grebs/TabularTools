# ADR-0006: Measure tests via query-scoped overrides; differential mode by default

- **Status:** Proposed
- **Date:** 2026-10-27

## Context

Measure regression tests must evaluate PR versions of measures. v1 never writes over XMLA, so the PR version can't be deployed. Linux CI has no local engine. Snapshot values go stale when data refreshes, and storing real aggregates in Git may be sensitive.

## Decision

- Evaluate against a deployed model, **read-only**, using **query-scoped definitions** (`DEFINE MEASURE` / `DEFINE COLUMN`) to inject base or head definitions of everything that differs between the two refs.
- **Differential mode (default):** run each case twice at the same moment (base overrides vs head overrides) and compare. Nothing is stored in Git, and data refreshes and deployment drift cancel out.
- **Snapshot mode (opt-in):** stored expected values, for pinned business facts.
- **Invariant assertions:** row-wise DAX conditions, no stored data.
- In differential mode, value changes on measures the PR edited *directly* are "expected changes" (non-blocking by default, blocking with `--strict`); changes elsewhere are regressions.
- Structural changes (relationships, types, partitions) are listed as **not testable** without deployment.
- An `IDaxExecutor` port, with `XmlaDaxExecutor` (live) and `RecordedDaxExecutor` (offline mock for CI).
- Test files are YAML (`*.measuretests.yaml`) with a JSON Schema; snapshot rows are columnar for readable diffs.

## Consequences

- Real regression signal on PRs without deployment permissions or scratch workspaces.
- Needs a deployed model with data and XMLA read access (Premium, PPU or Fabric).
- **To verify in M4:** that model measures referencing an overridden measure use the query definition, and how `DEFINE COLUMN` behaves on the service. If columns don't work, column changes are reported as structural.

## Implementation notes (M4)

- Overrides are the measures whose DAX changed (formatting-only changes excluded), renamed measures, and measures that exist on one side only. Each query defines only the overrides its measures depend on, so an unrelated change can't break it. Measures that only moved table aren't overridden.
- `DEFINE COLUMN` overrides of calculated columns are **opt-in** (`tests.columnOverrides`) until verified; by default calculated column changes are listed as not tested.
- Neither R3 question could be answered in the build environment (no tenant). `scripts/verify-live.ps1` answers both against the synthetic LiveCheck model; see docs/milestones/M4.md.

## Alternatives considered

- **Deploy the PR head to a scratch workspace:** most faithful, but requires XMLA writes (out of scope for v1) and capacity. Possible later behind an opt-in flag.
- **Snapshot-only:** brittle under refresh, and stores data in Git. Kept as opt-in only.
