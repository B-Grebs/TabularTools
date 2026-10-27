# ADR-0001: Own thin read layer; Microsoft's Authoring MCP server as an optional companion

- **Status:** Proposed
- **Date:** 2026-10-27

## Context

Microsoft ships the Power BI Authoring MCP Server (repo `microsoft/powerbi-modeling-mcp`, previously "Modeling"). npm `@microsoft/powerbi-modeling-mcp` reached 1.0.0 on 2026-09-25, with binaries for Windows, Linux and macOS. It provides CRUD for every model object type, DAX query and validation, traces, and TMDL import/export, against Desktop, Fabric workspaces, or a PBIP TMDL folder. Its repo LICENSE is MIT, but the README says use is subject to a separate EULA, which must be accepted before tools run.

Our value is the layer above CRUD: semantic diff, regression tests, lineage, and linting in CI. The brief asks whether to (a) build our own thin TOM/ADOMD access layer, or (b) recommend running Microsoft's server alongside ours for basic CRUD.

## Decision

Do **(a) for reading and querying**, and **(b) for editing**:

- Build our own thin layer that *reads* models (TMDL/TMSL files, Git blobs in memory, later XMLA) with `Microsoft.AnalysisServices` and *runs DAX queries* with ADOMD.NET.
- Don't build generic CRUD tools. Document Microsoft's server as an optional companion for agents that also edit models. Our tools work on the same PBIP folder, so the two compose.

## Consequences

- The diff engine and CI CLI work without any MCP client, Node.js or EULA acceptance. Both are necessary: diff needs two object graphs from two commits, and CI has no MCP client.
- Our write surface stays small (test files now, targeted refactorings later), so there's less to maintain as Power BI adds object types.
- Users who want natural-language editing install a second MCP server. Our `safe_refactor` prompt describes the edit loop without requiring it.
- If Microsoft's server later adds semantic diff or tests, we'll need to re-check what sets us apart (risk accepted; CI-first and PR-comment output are still ours).

## Alternatives considered

- **Depend on Microsoft's server for all model access.** Rejected: it can't read Git history, needs Node and EULA acceptance in CI, and makes an OSS product depend on EULA-governed software.
- **Build full CRUD ourselves.** Rejected: commodity, high maintenance, not where our value is.
