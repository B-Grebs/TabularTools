# ADR-0008: Read-only safety model: no XMLA writes, opt-in local writes, DAX query guard

- **Status:** Proposed
- **Date:** 2026-10-27

## Context

The XMLA endpoint is read/write, and identities used for testing often have write rights. AI agents call tools autonomously and may act on injected instructions in model metadata or PR text. The brief requires read-only by default, local PBIP/TMDL writes only, no XMLA writes in v1, and every write tool reporting exactly which files it changed.

## Decision

- The MCP server starts **read-only**: write tools are only registered with `--allow-writes`.
- **No XMLA write path exists in v1.** `Live` exposes queries (and, later, model reads) only. Deploy, if ever added, goes in a separate assembly behind `IModelDeployer`, an explicit flag, and a per-profile opt-in.
- **DAX query guard:** only queries starting with `DEFINE` / `EVALUATE`, or `$SYSTEM` DMV selects, are sent. TMSL/XMLA command text is rejected before connecting. Never `ExecuteNonQuery`.
- **All file writes go through one writer:** confined to workspace roots, atomic, dry run by default, returning `{path, action, unifiedDiff}` for every file.
- **TMDL edits are targeted** (one object's block), never a full re-serialization. M0 showed full serialization renames files.
- Tokens and queries go only to allowlisted hosts. Row caps on `run_dax`, and a switch to disable live tools.

## Consequences

- Safe to run in CI and in agent sessions by default. Users opt in to writes explicitly.
- Slightly more friction for write workflows; acceptable given the brief's priorities.

## Alternatives considered

- **Rely on MCP clients' per-call confirmation only:** not every client confirms, and CI has no human. Rejected as the only safeguard.
