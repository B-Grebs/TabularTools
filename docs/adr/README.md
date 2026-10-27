# Architecture Decision Records

Each significant decision gets a short record: context, decision, consequences, alternatives.
Status values: **Proposed** (awaiting owner review), **Accepted**, **Superseded by ADR-xxxx**.
All records below are **Proposed** until the M0 review.

| ADR | Title | Status |
|---|---|---|
| [0001](0001-own-read-layer-ms-server-optional.md) | Own thin read layer; Microsoft's Authoring MCP server as an optional companion | Proposed |
| [0002](0002-target-dotnet-10.md) | Target .NET 10 (LTS) | Proposed |
| [0003](0003-git-cli-not-libgit2sharp.md) | Read Git through the git CLI, not LibGit2Sharp | Proposed |
| [0004](0004-semantic-diff-on-object-graphs.md) | Semantic diff on object graphs, with lineage-tag-first rename detection | Proposed |
| [0005](0005-bpa-compatible-lint-rules.md) | BPA-compatible lint rules; vendor Microsoft's rules, load others by path | Proposed |
| [0006](0006-measure-tests-query-scoped-overrides.md) | Measure tests via query-scoped overrides; differential mode by default | Proposed |
| [0007](0007-explicit-lineage-config.md) | Explicit lineage config; sources found by parsing, not naming | Proposed |
| [0008](0008-read-only-safety-model.md) | Read-only safety model: no XMLA writes, opt-in local writes, DAX query guard | Proposed |
| [0009](0009-auth-own-token-acquisition.md) | Acquire Entra ID tokens ourselves and pass them to ADOMD | Proposed |
| [0010](0010-license-mit-vs-apache.md) | License: MIT vs Apache-2.0 | **Accepted — MIT** |
| [0011](0011-yaml-config-with-json-schema.md) | YAML for human-authored files, with JSON Schemas | Proposed |

Template: copy any record; keep it under a page.
