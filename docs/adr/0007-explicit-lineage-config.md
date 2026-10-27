# ADR-0007: Explicit lineage config; sources found by parsing, not naming

- **Status:** Proposed
- **Date:** 2026-10-27

## Context

Existing lineage tooling (a PowerShell dependency resolver) relies on ad-hoc repo and naming conventions. The brief says to design a clean, explicit config, find source objects by parsing partition M rather than by naming conventions, and assume nothing about any organization's layout.

## Decision

- A documented YAML config (`tabulartools.lineage.yaml`, JSON Schema in `schemas/`) declares repos (local path and/or Git URL + ref), include/exclude globs for semantic models, reports and optional SQL database projects, a `publishedModels` map for service-bound (`byConnection`) reports, and `sources.aliases` for mapping physical server/database names to logical ones.
- Reports link to models via `definition.pbir` `datasetReference` (`byPath` resolved relatively; `byConnection` via `publishedModels`).
- Models link to sources by **parsing M** (tolerant parser, `let` resolution, parameter substitution, constant folding) with a **data-driven connector catalog**, and native SQL via ScriptDom.
- Every edge carries evidence. Anything unresolvable is listed in `unresolved` with the raw expression; nothing is guessed.
- Remote repos are cloned with the user's own git credentials into a git-ignored cache.

## Consequences

- Teams must write a config, a one-time cost, but lineage becomes reproducible and reviewable.
- Some sources will be unresolved (dynamic M, custom functions); that's surfaced, not hidden.
- Field-level lineage (report visuals → fields) needs PBIR parsing, a v1 non-goal; the graph schema leaves room for it.

## Alternatives considered

- **Port the existing resolver's conventions:** explicitly ruled out by the brief.
- **Query the Power BI service (Scanner API) for lineage:** needs tenant admin APIs and isn't Git-based; a possible future source, not v1.
