# ADR-0004: Semantic diff on object graphs, with lineage-tag-first rename detection

- **Status:** Proposed
- **Date:** 2026-10-27

## Context

Reviewers need to know what a PR *means* for a model (DAX changed, relationship now bidirectional, RLS filter changed, object renamed), not which TMDL lines moved. TMDL text diffs are noisy: reordering, formatting, and GUID relationship names.

## Decision

- Deserialize both sides into TOM `Database` objects (in memory from Git blobs; verified in M0), then into an immutable `ModelSnapshot` with stable keys.
- Key relationships by endpoints, not GUID names. Match measures model-wide, since names are unique, so moves are detected.
- Match objects in order: **exact key → `lineageTag` (certain rename) → scored heuristic (likely rename, with confidence)**, then added/removed.
- Compare properties from a per-type **property catalog**. Classify expression changes as formatting-only when the token sequences match.
- Assign each change a severity from a configurable policy: `breaking`, `high`, `medium`, `low`, `info`.
- Text diffs appear only inside a single changed expression.

## Consequences

- Output is structured (JSON) plus Markdown for PR comments, with impact (downstream DAX dependents, affected reports via lineage).
- Rename detection is reliable for Desktop-authored models (lineage tags), and labeled as a guess otherwise.
- The property catalog must track new TOM properties (see ARCHITECTURE R2).

## Alternatives considered

- **Text diff of TMDL:** what Git already does; doesn't meet the goal. Rejected.
- **TMSL JSON diff (JSON Patch):** tracks structure, not meaning; ordering and noise problems; no rename notion. Rejected.
