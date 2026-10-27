# ADR-0011: YAML for human-authored files, with JSON Schemas

- **Status:** Proposed
- **Date:** 2026-10-27

## Context

Users author several files by hand: repo config, lineage config, and measure test files. Their audience works in ADO, where pipelines are YAML. Agents also author these files and benefit from a schema.

## Decision

- Human-authored files are **YAML** (YamlDotNet, MIT). JSON is accepted anywhere YAML is (it's a subset).
- Every file has a `schemaVersion` and a **JSON Schema** in `schemas/`, generated from C# types. The schemas are exposed as MCP resources and referenced via `# yaml-language-server: $schema=…` for editor validation.
- Lint rule files stay **BPA JSON** for ecosystem compatibility (ADR-0005).
- Machine outputs (diff, lint, lineage, test results) are JSON with `schemaVersion`.

## Consequences

- Readable, commentable files with editor completion and validation.
- YAML pitfalls (implicit typing such as `no` → false) are handled by schema-driven deserialization and by quoting in generated files.

## Alternatives considered

- **JSON everywhere:** no comments, noisier diffs for test snapshots. Rejected for human-authored files.
