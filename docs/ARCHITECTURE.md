# TabularTools — Architecture

> **Status:** Reviewed after M0 (license: MIT; other open questions taken at the proposed defaults). M1 is implemented; see `docs/milestones/`.
> **Working name:** `TabularTools` (the repo name). See [§19 Open questions](#19-open-questions-for-review) on naming.
> **Last updated:** 2026-10-27

This document describes the design of an MCP server and CLI that give AI agents and CI pipelines tools for **Power BI semantic models** stored as PBIP/TMDL in Git. The tools read models, lint them, compute semantic diffs between commits, run measure regression tests, and resolve lineage across repositories.

Facts marked **(verified)** were checked in a throwaway spike on Linux (.NET 10, `Microsoft.AnalysisServices` 19.118.3) during M0. Facts marked **(to verify)** could not be checked from the build environment and are tracked in [§18 Risks](#18-risks).

---

## Contents

1. [Goals and scope](#1-goals-and-scope)
2. [Decisions already made](#2-decisions-already-made)
3. [Landscape and the build-vs-reuse decision](#3-landscape-and-the-build-vs-reuse-decision)
4. [Components](#4-components)
5. [Model loading](#5-model-loading)
6. [MCP surface: tools, resources, prompts](#6-mcp-surface-tools-resources-prompts)
7. [CLI surface](#7-cli-surface)
8. [Data flow per tool](#8-data-flow-per-tool)
9. [Lint engine](#9-lint-engine)
10. [Semantic diff algorithm](#10-semantic-diff-algorithm)
11. [Measure tests and the test file format](#11-measure-tests-and-the-test-file-format)
12. [Lineage and the lineage config format](#12-lineage-and-the-lineage-config-format)
13. [Live access and the auth model](#13-live-access-and-the-auth-model)
14. [Safety model](#14-safety-model)
15. [Platform support](#15-platform-support)
16. [Configuration files](#16-configuration-files)
17. [Fixtures, testing and CI](#17-fixtures-testing-and-ci)
18. [Risks](#18-risks)
19. [Open questions for review](#19-open-questions-for-review)
20. [Milestone plan](#20-milestone-plan)

ADRs live in [`docs/adr/`](adr/README.md).

---

## 1. Goals and scope

**What it does.** One engine, two hosts:

- an **MCP server** (stdio) for Claude Code, Claude Desktop, VS Code and other MCP clients;
- a **CLI** for Azure DevOps (ADO) pipelines. It emits JSON or Markdown, posts PR comments, and exits non-zero on blocking findings.

**Goals, in priority order** (from the brief):

1. Useful right away for PR review and safe refactoring of semantic models in ADO Git.
2. Good enough to release as open source.
3. Keep a commercial offering possible. In practice this means nothing tenant-specific, no single way of working baked in, clean licensing, and a host-independent core.

**Non-goals for v1:** report pages and visuals (PBIR), deploying or writing over XMLA, data-platform integrations beyond reading source references from model metadata, a hosted or multi-tenant service, and any UI beyond MCP clients and the CLI.

**What sets it apart.** Generic "edit a measure" or "run a DAX query" tools are commodity features (see §3). The value here is the layer above them: **semantic diff, regression tests, lineage, and linting in CI**.

---

## 2. Decisions already made

| Topic | Decision | Where it shows up |
|---|---|---|
| XMLA | The endpoint is read/write. **v1 stays read-only by design.** No code path calls `SaveChanges`, sends TMSL, or runs non-query commands. | §13, §14, [ADR-0008](adr/0008-read-only-safety-model.md) |
| Platforms | ADO build agents are Windows. Local dev is Windows. This build session is Linux. All offline features must build and pass tests on Linux. | §15, [ADR-0002](adr/0002-target-dotnet-10.md) |
| Linting | No ruleset in use today. Baseline is the Microsoft / Tabular Editor default BPA rules, plus extensibility. | §9, [ADR-0005](adr/0005-bpa-compatible-lint-rules.md) |
| Employer separation | **Hard rule:** the repo contains nothing about the owner's employer — no names, tenant or workspace IDs, model names, repo names, or data. All examples and fixtures are synthetic. Hostnames use the reserved `.invalid` TLD; IDs are all-zero GUIDs. | §17 |
| Lineage | Existing conventions are ad-hoc. Design a clean, explicit config instead of porting them. | §12, [ADR-0007](adr/0007-explicit-lineage-config.md) |
| Data platform | No Databricks or other platform integration in v1. | §1 |
| License | **MIT** (owner decision, 2026-10-27). Comparison in [ADR-0010](adr/0010-license-mit-vs-apache.md). | ADR-0010 |

---

## 3. Landscape and the build-vs-reuse decision

### 3.1 What exists (as of 2026-10-27)

| Product | What it does | Notes from M0 research |
|---|---|---|
| **Power BI Authoring MCP Server** (repo `microsoft/powerbi-modeling-mcp`, previously called the Power BI *Modeling* MCP server) | Local MCP server. Connects to Power BI Desktop, Fabric workspaces, or a PBIP TMDL folder. Provides CRUD for every object type, transactions, DAX query and validation, traces, and TMDL import/export. | npm `@microsoft/powerbi-modeling-mcp` reached **1.0.0 on 2026-09-25**. Ships binaries for win/linux/darwin. The repo `LICENSE` is MIT, but the README says **use is subject to a EULA**, which must be accepted before tools run. Has a `--readonly` flag and a connection-host allowlist. GA status could not be confirmed from Microsoft docs, because learn.microsoft.com is blocked from the build environment. |
| **Remote Power BI MCP servers** (hosted) | A hosted authoring server for Fabric workspaces, and a hosted server for natural-language querying of published models. | Hosted, so it can't run in an offline CI job or read Git history. |
| **Tabular Editor 2/3 + Best Practice Analyzer** | The established tools for model scripting and linting. | TE2 is MIT. TE3 is commercial. BPA rules use TE's Dynamic LINQ dialect over TE's object model (see §9). |

### 3.2 Decision: our own thin read layer; Microsoft's server as an optional companion

> **Recommendation: (a) build our own thin TOM/ADOMD access layer**, scoped to *reading* models (from files, from Git blobs, and later from XMLA) and *running DAX queries*. Do not build generic CRUD editing tools. Document Microsoft's Authoring MCP server as an **optional companion** for agents that also need to edit models, and design the two to compose. See [ADR-0001](adr/0001-own-read-layer-ms-server-optional.md).

Why:

1. **We need our own read layer regardless.** The diff engine has to build two object graphs from two Git commits. The CI CLI has to run with no MCP client and no Node.js. Neither need is covered by Microsoft's server. The read layer is small: TMDL deserialization is a single library call (verified), and DAX queries go through ADOMD.NET.
2. **No dependency on a EULA-governed product.** An open-source, and possibly commercial, product can't bundle or hard-depend on software under a separate EULA, or require users to accept one to run our CI checks.
3. **Don't duplicate commodity features.** CRUD on measures and columns is what Microsoft's server does well, and keeping up with every new object type is costly. Our write surface stays narrow: test files now, targeted refactorings later (§14.3).
4. **They compose naturally.** An agent can edit a PBIP folder with Microsoft's server (PBIP mode writes TMDL), then call our `diff_model`, `lint_model` and `run_measure_tests` on the same folder. The `safe_refactor` prompt (§6.4) describes that loop without depending on Microsoft's server.

---

## 4. Components

### 4.1 Solution layout

```
TabularTools.sln
src/
  TabularTools.Core/        Offline engine. No MCP, CLI, network or auth concerns.
  TabularTools.Live/        XMLA/ADOMD query execution + Entra ID token providers. (Added in M4.)
  TabularTools.Mcp/         MCP stdio host (thin): tool/resource/prompt definitions → Core/Live.
  TabularTools.Cli/         CLI host (thin): commands → Core/Live; ADO PR comment publisher.
tests/
  TabularTools.Core.Tests/  Unit + fixture tests (Linux and Windows).
  TabularTools.Live.Tests/  Mock-based tests; live tests opt-in via env var, skipped by default. (M4.)
  TabularTools.Mcp.Tests/   In-process MCP client ↔ server round trips.
  TabularTools.Cli.Tests/   Command, exit-code and output-format tests.
fixtures/                   Synthetic PBIP models, diff snapshots, lineage repos (§17).
samples/azure-pipelines/    Sample ADO pipeline YAML for a Windows agent.
schemas/                    JSON Schemas for config, test files, lineage config, outputs.
rules/                      Vendored Microsoft BPA rules (MIT, with notice) + built-in rule docs.
docs/                       This document, ADRs, milestone notes.
```

The brief asks for one core plus two hosts. This design adds one refinement: **`Live` is its own assembly.** It holds ADOMD.NET, MSAL/Azure.Identity, and the code that is (partly) Windows-only. That keeps `Core` fully offline, testable on Linux, and free of auth dependencies. The hosts reference both.

### 4.2 Component diagram

```
          MCP client (Claude Code / Desktop / VS Code)          ADO pipeline (Windows agent)
                         │ stdio (JSON-RPC)                                │ process
                ┌────────▼─────────┐                              ┌────────▼─────────┐
                │ TabularTools.Mcp │                              │ TabularTools.Cli │──► ADO REST (PR threads)
                └────────┬─────────┘                              └────────┬─────────┘
                         └──────────────────┬──────────────────────────────┘
                                            │ same operations, same result types
   ┌────────────────────────────────────────▼──────────────────────────────────────────┐
   │ TabularTools.Core                                                                  │
   │  Workspace & config ─ ModelSources (folder │ git blob │ bim) ─► ModelSnapshot         │
   │  Analysis: DAX lexer + dependency graph │ M lexer/parser + source extractor │ SQL refs│
   │  Engines:  Lint │ Diff │ MeasureTests (plan/compare) │ Lineage                          │
   │  Rendering: JSON (versioned schemas) │ Markdown │ (later) SARIF                         │
   │  Ports: IGitReader │ IDaxExecutor │ IFileWriter │ IClock                                │
   └───────────────▲──────────────────────────────────────────────▲─────────────────────┘
                   │ implements IDaxExecutor                       │ implements IGitReader
   ┌───────────────┴──────────────┐                    ┌───────────┴───────────┐
   │ TabularTools.Live            │                    │ GitCliReader (Core)   │──► git CLI
   │  XmlaDaxExecutor (ADOMD.NET) │──► powerbi://…     └───────────────────────┘
   │  RecordedDaxExecutor (mock)  │
   │  TokenProviders (Azure.Id.)  │──► Entra ID
   └──────────────────────────────┘
```

### 4.3 Core modules

| Module | Responsibility |
|---|---|
| `Workspace` | Resolves roots, loads and merges `tabulartools.yaml` and local config, and resolves connection profiles (never secrets — §16). |
| `Model` | `IModelSource` implementations, PBIP layout discovery, and the immutable `ModelSnapshot` (§5.3), plus a content-hash cache. |
| `Dax` | Hand-written DAX lexer, reference resolver, and model-wide dependency graph (`DependsOn` / `ReferencedBy`). Token-normalized comparison. TE-compatible token type names for BPA `Tokenize()`. |
| `PowerQuery` | M lexer and tolerant recursive-descent parser for the practical subset (let/in, records, lists, invocations, field and item access, `meta`, literals, `#"quoted"` identifiers, `&` concatenation). Constant folding plus parameter substitution. A connector catalog maps connector functions to source references. |
| `Sql` | Extracts table and view references from native SQL with `Microsoft.SqlServer.TransactSql.ScriptDom` (MIT, cross-platform), with a tolerant tokenizer fallback for non-T-SQL dialects (low confidence). |
| `Lint` | Rule loading (BPA JSON + built-in C# rules), the TE-compatible object view, the Dynamic LINQ dialect adapter, suppressions, and diff-aware mode. |
| `Diff` | Object matching (key → lineage tag → heuristic), the property catalog, change classification, severity policy, and impact analysis. |
| `Testing` | Test file model, query planning (including query-scoped overrides), result comparison with tolerances, and snapshot writing. Execution goes through `IDaxExecutor`. |
| `Lineage` | Config model, repo materialization through `IGitReader`, discovery of models, reports and database projects, and graph building and traversal. |
| `Rendering` | Stable JSON (each top-level result carries `schemaVersion`) and Markdown renderers tuned for ADO PR comments. |
| `Writes` | The single write path: sandboxed file writes with dry-run previews and a changed-files report (§14.3). |

### 4.4 Key technology choices

| Concern | Choice | ADR |
|---|---|---|
| Runtime | **.NET 10 (LTS, supported to Nov 2028).** .NET 8 support ends 2026-11-10, two weeks from now. TOM ships `net8.0` assemblies, which run on .NET 10 (verified). | [0002](adr/0002-target-dotnet-10.md) |
| TOM / TMDL | `Microsoft.AnalysisServices` 19.118.x (the unified package that replaces `*.NetCore.retail.amd64`). | [0001](adr/0001-own-read-layer-ms-server-optional.md) |
| Queries | `Microsoft.AnalysisServices.AdomdClient` 19.118.x. | [0009](adr/0009-auth-own-token-acquisition.md) |
| MCP | `ModelContextProtocol` 2.3.x (Apache-2.0), stdio transport. Uses tool annotations (`ReadOnly`, `Destructive`, `Idempotent`, `OpenWorld`) and structured output (verified present). | — |
| Git | **git CLI** behind `IGitReader` (not LibGit2Sharp). | [0003](adr/0003-git-cli-not-libgit2sharp.md) |
| Auth | Azure.Identity credential chain, with tokens passed to ADOMD through `AccessToken`. | [0009](adr/0009-auth-own-token-acquisition.md) |
| Human-authored files | YAML (YamlDotNet), with JSON Schemas in `schemas/` for editor validation. JSON is accepted anywhere YAML is. | [0011](adr/0011-yaml-config-with-json-schema.md) |
| Lint rule files | BPA JSON (format compatible with Tabular Editor). | [0005](adr/0005-bpa-compatible-lint-rules.md) |
| Rule expressions | `System.Linq.Dynamic.Core` (Apache-2.0) behind a dialect adapter. | [0005](adr/0005-bpa-compatible-lint-rules.md) |
| CLI parsing | `System.CommandLine` 2.0.x (MIT). | — |

---

## 5. Model loading

### 5.1 Supported inputs

| Input | Detected by | Loader |
|---|---|---|
| PBIP project file | `*.pbip` | Follows its `artifacts` to the report, then the report's `byPath` reference to the semantic model; or finds sibling `*.SemanticModel` folders. |
| Semantic model folder | `*.SemanticModel/` with `definition.pbism` | Uses `definition/` (TMDL) if present, otherwise `model.bim` (TMSL). |
| TMDL folder | contains `database.tmdl` or `model.tmdl` | `TmdlSerializer.DeserializeDatabaseFromFolder` **(verified)** |
| TMSL file | `model.bim` | `JsonSerializer.DeserializeDatabase` **(verified)** |
| Git ref + path | `{repo, ref, path}` | `IGitReader` lists blobs → `MetadataSerializationContext.ReadFromDocument(...)` per file → `ToDatabase(...)`. **No checkout needed (verified in memory).** |
| Fabric Git-integration folder | `*.SemanticModel/` with `.platform` | Same layout as PBIP. `.platform` metadata (`displayName`, `logicalId`) is recorded. |
| XMLA endpoint (later) | `powerbi://…` / `asazure://…` | TOM `Server.Connect` + read the `Database`. Read-only, never `SaveChanges`. Planned after M5. |

Quirks found in the spike:

- PBIP `database.tmdl` has **no database name**. The loader sets the name from the `*.SemanticModel` folder name, because TMSL serialization fails with an empty name (verified).
- TMDL errors carry useful locations. `TmdlFormatException` exposes `Document`, `Line` and `LineText`. Unresolved references raise `TmdlDeserializationWithReferenceErrorsException` with a list (verified). Both become structured diagnostics: `{code, message, file, line}`.

### 5.2 Model handles

`open_model` returns a `modelId` handle (an opaque string). **Every tool that takes a model also accepts a path directly**, because LLMs often drop handles between turns. Models are cached by `(source identity, content hash)`, so repeated calls are cheap and edits made on disk by other tools are picked up automatically (the hash changes). The cache is LRU with a size bound.

### 5.3 `ModelSnapshot`

TOM objects are mutable and heavy, and TOM has **no DAX dependency API** (verified). Engines therefore work on an immutable `ModelSnapshot` built once per load:

- an object tree with **stable keys** (`table:Sales`, `table:Sales/measure:Total Sales`, `relationship:Sales[DateKey]->Date[DateKey]`, `role:West Region/tablePermission:Sales`);
- per-object semantic properties, read through one property catalog shared by diff and describe;
- `LineageTag` where present (TOM 19.118 exposes it; Desktop writes it into PBIP), used for rename detection;
- the DAX dependency graph, the M source extraction per partition and shared expression, and a source-location index (object → TMDL file and line, best effort, found by scanning declarations).

Supported object types follow TOM 19.118. The spike confirmed it includes newer types such as `Function` (DAX user-defined functions), `Calendar`, `CalculationGroup`, `QueryGroup`, `EntityPartitionSource` (Direct Lake), `FormatStringDefinition` and `RefreshPolicy`.

---

## 6. MCP surface: tools, resources, prompts

### 6.1 Conventions

- **Naming:** `verb_noun`, snake_case.
- **Descriptions:** crisp, saying *when* to call the tool and what it returns. The strings below are the proposed final text.
- **Output:** every tool returns structured content (JSON, `UseStructuredContent = true`, with an output schema) **and** a short human-readable text summary. The JSON follows the same versioned schemas as the CLI's `--format json`.
- **Diagnostics:** results include `diagnostics: [{level, code, message, file?, line?}]` instead of failing the whole call where partial results are useful.
- **Size control:** large outputs support `limit` + `cursor` and report `truncated: true`, so the LLM's context isn't flooded.
- **Annotations:** every tool declares `ReadOnly`, `Destructive`, `Idempotent` and `OpenWorld` (openWorld = talks to a live service or remote Git).
- **Model parameter:** `model` is a path *or* a handle (§5.2). `model` is optional when exactly one model is found under the workspace root.

### 6.2 Tools

The brief's names are kept except where the scope widened: `diff_pbip` → `diff_model` (it also diffs `model.bim` and plain folders) and `gen_measure_tests` → `generate_measure_tests`. **`get_dependencies` is new**, because it is the core primitive for safe refactoring.

| Tool | RO | Open world | Milestone |
|---|---|---|---|
| `open_model` | ✅ | ❌ (✅ for XMLA later) | M1 |
| `describe_model` | ✅ | ❌ | M1 |
| `get_dependencies` | ✅ | ❌ | M2 (needed by lint) |
| `lint_model` | ✅ | ❌ | M2 |
| `diff_model` | ✅ | ❌ (local git only) | M3 |
| `run_dax` | ✅ | ✅ | M4 |
| `generate_measure_tests` | ❌ (writes a test file when `save: true`) | ✅ | M4 |
| `run_measure_tests` | ✅ | ✅ | M4 |
| `resolve_lineage` | ✅ | ✅ only if it clones remote repos | M5 |

Schemas are shown in compact TypeScript notation. The real JSON Schemas are generated from C# types into `schemas/`.

#### `open_model`

> *Load a Power BI semantic model from a PBIP project, a `*.SemanticModel` folder, a TMDL `definition` folder, or a `model.bim` file. Returns a handle plus a summary (counts, compatibility level, load diagnostics). Call this first to validate that a model parses; other tools also accept the path directly.*

```ts
in:  { path: string }                              // later: { connection: { profile: string } }
out: { modelId: string, name: string, source: { kind: "pbip"|"tmdl"|"bim"|"git", path: string },
       compatibilityLevel: number, counts: { tables, columns, measures, relationships, roles,
       calculationGroups, functions, partitions }, diagnostics: Diagnostic[] }
```

#### `describe_model`

> *List the contents of a loaded semantic model — tables, columns, measures, relationships, roles, calculation groups, partitions — at a chosen level of detail. Use `detail: "summary"` to orient, then narrow with `tables` / `objectTypes` / `namePattern` and `detail: "full"` to read DAX and M expressions.*

```ts
in:  { model: string, detail?: "summary"|"objects"|"full" = "objects",
       tables?: string[], objectTypes?: ObjectType[], namePattern?: string /* glob */,
       includeHidden?: boolean = true, limit?: number = 200, cursor?: string }
out: { model: ModelRef, objects: ObjectInfo[], relationships?: RelationshipInfo[],
       roles?: RoleInfo[], truncated: boolean, nextCursor?: string }
// ObjectInfo: { key, type, table?, name, isHidden, dataType?, summarizeBy?, formatString?,
//               displayFolder?, description?, expression? (full only), lineageTag? }
```

#### `get_dependencies`

> *Show what a measure, column, table, calculation item or RLS filter depends on (upstream) and what depends on it (downstream), resolved from DAX expressions across the whole model. Use before renaming, deleting or changing an object, to see what would break.*

```ts
in:  { model: string, object: string /* "Sales[Amount]" | "[Total Sales]" | "'Date'" */,
       direction?: "upstream"|"downstream"|"both" = "both", depth?: number = 1 /* 0 = transitive */ }
out: { object: ObjectRef, upstream: DepEdge[], downstream: DepEdge[],
       usedIn: { relationships: string[], sortBy: string[], hierarchies: string[], rls: string[] } }
// DepEdge: { from: ObjectRef, to: ObjectRef, fullyQualified: boolean, via: "dax"|"sortBy"|... }
```

#### `lint_model`

> *Check a semantic model against best-practice rules (Microsoft BPA rules, built-in rules, and team rule files) and return violations with rule, object, severity and a fix hint. Pass `baseRef` to report only violations introduced since that git ref (for PR review).*

```ts
in:  { model: string, rulesets?: string[] /* ids, file paths or URLs */, minSeverity?: Sev,
       baseRef?: string, repo?: string, ruleIds?: string[] }
out: { model: ModelRef, rulesets: RulesetInfo[], violations: Violation[],
       skippedRules: { ruleId, reason }[],             // e.g. unsupported expression — never silent
       summary: { error, warning, info, new?, existing? } }
// Violation: { ruleId, ruleName, category, severity: "error"|"warning"|"info", object: ObjectRef,
//              message, fixHint?, fixExpression?, isNew?: boolean, location?: { file, line } }
```

#### `diff_model`

> *Compare two versions of a semantic model by meaning, not text — between two git refs (e.g. a PR's target and source) or two folders. Reports changed DAX, relationship direction/cardinality, data types, summarize-by, RLS filters, and added/removed/renamed objects, each with a severity and the objects affected downstream.*

```ts
in:  { repo?: string = ".", base: string /* ref or folder */, head?: string = "HEAD" /* ref, folder or "WORKTREE" */,
       modelPath?: string /* omit: diff every model changed between refs */,
       mergeBase?: boolean = true, minSeverity?: Sev, includeFormattingOnly?: boolean = false }
out: { schemaVersion: 1, base: Side, head: Side, models: ModelDiff[] }
// ModelDiff: { modelPath, summary: Record<Severity, number>, changes: Change[], markdown?: string }
// Change: see §10.6
```

#### `run_dax`

> *Run a read-only DAX query (EVALUATE/DEFINE) against a live semantic model through a named connection profile, and return the result table. Rejects anything that is not a DAX query or a $SYSTEM DMV select. Rows are capped.*

```ts
in:  { profile: string, query: string, maxRows?: number = 500, timeoutSeconds?: number = 120,
       role?: string /* evaluate as RLS role; requires model admin rights */ }
out: { columns: { name, dataType }[], rows: unknown[][], rowCount: number, truncated: boolean,
       durationMs: number, target: { endpoint, database } }
```

#### `generate_measure_tests`

> *Create or extend a measure regression test file: pick measures and slices (group-by columns and filters) and the tool writes the test cases; in snapshot mode it also captures current values from a live model. Returns the file content and, when `save` is true, the files changed.*

```ts
in:  { model: string, testFile: string, measures: string[], groupBy?: string[], filters?: string[],
       mode?: "differential"|"snapshot" = "differential", profile?: string /* required for snapshot */,
       caseId?: string, save?: boolean = false }
out: { testFile: string, yaml: string, cases: string[], write: WriteResult /* §14.3 */ }
```

#### `run_measure_tests`

> *Run measure regression tests from a test file against a live model. In differential mode it evaluates each case with the base and head definitions of changed measures (no deployment needed) and reports which outputs changed; in snapshot mode it compares against stored values. Returns pass/fail per assertion.*

```ts
in:  { testFile: string, profile?: string, mode?: "differential"|"snapshot",
       repo?: string, base?: string, head?: string = "WORKTREE", caseIds?: string[] }
out: { summary: { passed, failed, changed, errored, skipped }, structuralChangesNotTested: Change[],
       results: { caseId, assertion, status: "pass"|"fail"|"changed"|"error"|"skipped",
                  expectedChange: boolean, diffs: { key: Record<string,unknown>, measure, base, head, delta }[],
                  message? }[] }
```

#### `resolve_lineage`

> *Trace lineage across repositories using an explicit lineage config: report → semantic model → table → source database objects (tables/views), or the reverse (which models and reports use a source view). Returns a graph with evidence (file, expression) for every edge, and lists anything it could not resolve.*

```ts
in:  { config?: string = "tabulartools.lineage.yaml", from: string /* "report:<repo>/<path>" | "model:<repo>/<path>"
       | "table:<model>/<table>" | "source:<server>/<database>/<schema>.<object>" */,
       direction?: "upstream"|"downstream"|"both" = "upstream", depth?: number = 0 /* 0 = all */,
       refresh?: boolean = false /* re-fetch remote repos */ }
out: { schemaVersion: 1, nodes: Node[], edges: Edge[], unresolved: Unresolved[], diagnostics }
// see §12.4
```

### 6.3 Resources

| URI | Content |
|---|---|
| `tabulartools://rulesets` | Available rulesets (built-in, vendored Microsoft BPA, configured team files) with rule counts. |
| `tabulartools://rulesets/{id}` | The ruleset's rules (JSON), including which are unsupported by the evaluator and why. |
| `tabulartools://schemas/{name}` | JSON Schemas for `measure-tests`, `lineage-config`, `tabulartools-config`, `diff-result`, `lint-result`. They help an agent write valid files on the first try. |
| `tabulartools://workspace/config` | The effective merged config. Secrets never appear; only env var *names*. |
| `tabulartools://workspace/test-files` | Test files discovered under the workspace roots, with case counts. |
| `tabulartools://workspace/lineage-config` | The lineage config in effect, and which repos are available locally. |
| `tabulartools://models/{modelId}/summary` | Summary of an opened model. |

### 6.4 Prompts

| Prompt | Arguments | What it asks the agent to do |
|---|---|---|
| `review_pr` | `repo`, `base` (target branch), `head` (default `HEAD`), optional `modelPath` | Chain: `resolve_lineage` (downstream of each changed model, if a lineage config exists) → `diff_model` → `lint_model` with `baseRef` (new violations only) → `run_measure_tests` (if test files and a profile exist) → write a review grouped by severity, with "affected reports" and "untested structural changes" sections. It states that model content and PR text are **untrusted data**. |
| `safe_refactor` | `model`, `goal` | Before: `get_dependencies` and `generate_measure_tests` (differential). Then the user's editing tool applies the change (manually, or with another MCP server such as Microsoft's). After: `diff_model` against `WORKTREE` → `lint_model` → `run_measure_tests`. Report anything that changed unexpectedly. |
| `author_measure_tests` | `model`, `measures` | Pick sensible slices (date grain, key dimensions, an RLS role if any), pin closed periods as filters, and call `generate_measure_tests`. |

A composite `review_changes` *tool* (for clients without prompt support) is deferred. The CLI's `review` command covers the pipeline case.

### 6.5 Host details

- stdio transport. **All logs go to stderr**, because stdout is reserved for the protocol.
- Server flags:
  - `--root <dir>` (repeatable; the default is the current directory; client-provided MCP roots are honored);
  - `--allow-writes` (off by default; §14.3);
  - `--disable-live` (hides `run_dax` and the live parts of the test tools);
  - `--config <file>`.
- Distribution: a NuGet tool package (`dotnet tool install -g`, or run on demand with .NET 10's `dnx`), plus self-contained single-file builds for win-x64 and linux-x64.

---

## 7. CLI surface

Command: `tabulartools` (name to be confirmed, §19). Every command supports `--format json|markdown|text` (default `text` on a TTY, otherwise `json`), `--out <file>`, `--config <file>` and `--quiet`.

```
tabulartools describe <model> [--detail summary|objects|full] [--tables ...]
tabulartools deps     <model> <object> [--direction upstream|downstream|both] [--depth N]
tabulartools lint     <model> [--rules <id|path|url>]... [--base <ref>] [--fail-on error|warning|info|none]
tabulartools diff     [--repo .] --base <ref|dir> [--head <ref|dir|WORKTREE>] [--model <path>] [--fail-on breaking|high|...|none]
tabulartools dax      --profile <name> (--query <dax> | --file <file.dax>) [--max-rows N]
tabulartools test gen <testfile> --model <path> --measures ... [--group-by ...] [--mode differential|snapshot] [--profile <name>]
tabulartools test run <testfile>... [--profile <name>] [--mode ...] [--base <ref>] [--head <ref>] [--strict]
tabulartools lineage  --from <object> [--config <file>] [--direction ...] [--format json|markdown|mermaid]
tabulartools review   [--repo .] --base <ref> [--head <ref>] [--model <path>]   # diff + lint(new) + tests + lineage in one report
tabulartools ado comment --file <review.md> [--marker <key>]                     # create or update a PR thread
tabulartools rules list|show <id>|check <file>                                   # inspect rulesets; validate a rule file
```

**Exit codes** (the same for every command):

| Code | Meaning |
|---|---|
| 0 | Success. No blocking findings. |
| 1 | Blocking findings at or above the `--fail-on` threshold: lint violations, test failures or errors, or diff changes. |
| 2 | Usage or config error (bad arguments, invalid config or test file). |
| 3 | Model load or parse error (the TMDL or TMSL is invalid). |
| 4 | Environment error (auth, network, git not found, shallow clone missing the base ref). |

Default thresholds: `lint --fail-on error`, `test run` fails on `fail` or `error` (and on `changed` with `--strict`), `diff --fail-on none` (informational). All are configurable in `tabulartools.yaml`.

**ADO PR comments.** `ado comment` upserts one thread per marker. It looks for an existing thread whose first comment contains `<!-- tabulartools:<marker> -->` (`GET …/pullRequests/{id}/threads`). It then updates that comment (`PATCH …/threads/{t}/comments/{c}`) or creates a thread (`POST …/threads`), using `api-version=7.1`. It reads `SYSTEM_COLLECTIONURI`, `SYSTEM_TEAMPROJECT`, `BUILD_REPOSITORY_ID`, `SYSTEM_PULLREQUEST_PULLREQUESTID` and `SYSTEM_ACCESSTOKEN` from the environment. Markdown over ~140,000 characters is truncated, with a pointer to the full report published as a pipeline artifact. The REST client sits behind `IPullRequestCommenter`, with a fake for tests. **(Real ADO calls can't be verified from the build environment — marked unverified in M3 notes.)**

---

## 8. Data flow per tool

**`open_model` / `describe_model`**

1. Resolve the path (§5.1).
2. Hash the source files and check the cache.
3. Deserialize to a TOM `Database`.
4. Build the `ModelSnapshot` (keys, properties, lineage tags, location index). The DAX graph and M extraction are built lazily.
5. Return the summary, or the filtered, paginated object list.

**`get_dependencies`**

1. Load the snapshot.
2. Build the DAX graph if needed: tokenize every DAX-bearing expression (measures, calculated columns and tables, calculation items, KPIs, detail rows, format string expressions, RLS filters, UDFs), then resolve references against model names.
3. Add structural uses (relationships, sort-by, hierarchies, variations).
4. Walk the graph to the requested depth.

**`lint_model`**

1. Load the snapshot.
2. Resolve rulesets from config and arguments: built-in, the vendored Microsoft set, and team files or URLs (URLs are cached by content hash and are opt-in).
3. Compile each BPA expression through the dialect adapter. Rules that fail to compile go to `skippedRules`.
4. For each rule scope, enumerate matching objects through the TE-compatible view, evaluate, and apply suppressions.
5. If `baseRef` is given: load the base snapshot from Git, lint it too, match objects with the diff engine's matcher (so renames are understood), and mark violations `isNew`.
6. Render.

**`diff_model`**

1. Resolve sides. For refs, use `git rev-parse`; with `mergeBase`, base = `merge-base(base, head)`, matching what a PR shows.
2. Find model paths. Use the given path, or `git diff --name-only base head` mapped to the `*.SemanticModel` folders it touches.
3. For each model, load both snapshots from blobs (no checkout).
4. Match objects, compare properties, classify changes, and apply the severity policy (§10).
5. Compute downstream impact in the head model, and affected reports if a lineage config exists.
6. Render JSON and Markdown.

**`run_dax`**

1. Validate the query with the DAX lexer: the first significant token must be `DEFINE` or `EVALUATE`, or the query must be a `SELECT … FROM $SYSTEM.*` DMV.
2. Resolve the profile, then get a token from the token provider.
3. Run `AdomdCommand.ExecuteReader` with a timeout.
4. Stream rows up to `maxRows` and map types.
5. Return.

**`generate_measure_tests`**

1. Load the model and check that the measures and columns exist (using the DAX resolver).
2. Build test cases.
3. In snapshot mode, build the query (§11.3), run it through the profile, and capture the result.
4. Serialize YAML, merging into an existing file and preserving other cases.
5. Write through the `Writes` module (dry run unless `save`).

**`run_measure_tests`**

1. Parse and validate the test file.
2. In differential mode: use the diff engine to find the measures (and calculated columns) whose definitions differ between base and head, plus every DAX dependency they pull in. List structural changes that can't be simulated in a query.
3. For each case, plan two queries (base overrides, head overrides). Run them; cases can run in parallel up to a limit.
4. Compare row by row by group key, with tolerances.
5. Classify each result: `changed` on a measure the PR edited directly counts as an *expected change*; `changed` on any other measure is a *regression* and counts as `fail`.
6. Render.

**`resolve_lineage`**

1. Load the config.
2. Materialize each repo: use the local `path` if present, otherwise clone or fetch `url@ref` into the cache with the git CLI, using the user's git credentials.
3. Discover models, reports and database projects using the globs.
4. Index:
   - reports → `definition.pbir` `datasetReference` (`byPath` resolved relatively; `byConnection` matched through `publishedModels`);
   - models → tables → partitions and shared expressions → M parse → source references;
   - database projects → `CREATE TABLE` / `CREATE VIEW` definitions (ScriptDom).
5. Build the graph and traverse from `from` in the requested direction.
6. Return nodes, edges with evidence, and unresolved items.

---

## 9. Lint engine

### 9.1 Rule sources and precedence

1. **Built-in rules** (C#, IDs `TT0xx`). These are checks that BPA expressions can't express well, or that need the dependency graph or relationship graph:
   - `TT001` broken DAX reference (an expression references a missing object) — error;
   - `TT002` ambiguous filter paths caused by bidirectional relationships — warning;
   - `TT003` RLS filter references a column outside its table, or an unknown column — error;
   - `TT004` hard-coded server or database literal in a partition instead of an M parameter — info;
   - `TT005` measure name collides with a column name — warning.
2. **Vendored Microsoft BPA rules** — `rules/microsoft-bpa/BPARules.json` from `microsoft/Analysis-Services`, **MIT, © 2016 Microsoft**, kept with its license notice in `THIRD_PARTY_NOTICES.md`. It has 71 rules (Performance 24, Formatting 15, DAX 12, Maintenance 9, Error Prevention 8, Naming 3). Updated deliberately by a script that records the upstream commit.
3. **Tabular Editor's community rules** (`TabularEditor/BestPracticeRules`) — **not vendored**. The repo has **no license file** (checked `LICENSE`, `LICENSE.md`, `LICENSE.txt`, `COPYING`), which means all rights are reserved. Users point a config entry at their own copy (a path or URL).
4. **Team rule files** — any BPA-format JSON file, referenced from `tabulartools.yaml`.
5. **Rules embedded in the model** — the `BestPracticeAnalyzer` model annotation that Tabular Editor uses. Off by default; on with `lint.useModelRules: true`.

### 9.2 Rule format

The format is **BPA JSON, accepted verbatim**: `ID, Name, Category, Description, Severity, Scope, Expression, FixExpression, CompatibilityLevel, Remarks`. One optional extension object is added, which Tabular Editor ignores:

```json
{
  "ID": "EXAMPLE_MEASURES_IN_DISPLAY_FOLDER",
  "Name": "Visible measures must have a display folder",
  "Category": "Maintenance",
  "Severity": 2,
  "Scope": "Measure",
  "Expression": "IsVisible and string.IsNullOrWhitespace(DisplayFolder)",
  "x-tabulartools": { "severity": "warning", "tags": ["layout"], "helpUrl": "https://example.invalid/rules/measures-folder" }
}
```

**Severity mapping:** BPA `1` → `info`, `2` → `warning`, `3`–`5` → `error`. This can be overridden per rule in config.

### 9.3 Evaluating Tabular Editor's expression dialect

BPA expressions are Dynamic LINQ over **Tabular Editor's wrapper object model**, not raw TOM. M0 analysis of the Microsoft set found these TE-specific members and functions in use:

- *Relations:* `DependsOn` (with `Key.ObjectType`, `Value.Any(FullyQualified)`), `ReferencedBy`, `UsedInRelationships`, `UsedInSortBy`, `UsedInHierarchies`, `UsedInVariations`, `UsedByPartitions`.
- *Collections:* `AllMeasures`, `AllColumns`, `AllPartitions`, `AllCalculationItems`, `RowLevelSecurity[...]`, `ObjectLevelSecurity`, `InPerspective[...]`, `TranslatedNames`.
- *Helpers:* `GetAnnotation()`, `ObjectTypeName`, `DaxObjectName`, `IsVisible`, `Tokenize()` (with `Type`, `Next`, and token names such as `DIV` and `INTEGER_LITERAL`), `SourceExpression`, `DataSource`.
- *Dialect keywords:* `it`, `outerIt`/`OuterIt`, `current`, `RegEx.IsMatch`, `Convert.*`, `string.IsNullOrWhitespace`, `char.*`.

Design:

- **`TeCompatView`**: a read-only facade over `ModelSnapshot` that exposes TE's property and method names for the members above. Semantics are ported from Tabular Editor 2's TOMWrapper (MIT, attributed in `THIRD_PARTY_NOTICES.md`).
- **Dialect adapter**: rewrites TE keywords to `System.Linq.Dynamic.Core` equivalents (`outerIt` → `parent`, `current` → `root`). It registers `RegEx`, `Convert` and `Math` as accessible types, and maps TE token type names onto our DAX lexer's tokens.
- **No silent passes.** A rule that fails to compile, or uses an unknown member, appears in `skippedRules` with a reason. A **compatibility harness test** compiles and runs all 71 Microsoft rules against the fixture. The M2 milestone notes will publish a coverage table. Target: ≥ 90% of rules evaluable at M2.
- Rules that depend on VertiPaq statistics annotations (e.g. `Vertipaq_Cardinality`) evaluate as "no data". The docs explain that offline linting can't see cardinality.
- `FixExpression` is returned as a hint. Applying fixes automatically (`lint --fix`) is a write feature, deferred until after M5 (§14.3).

### 9.4 Suppressions and diff-aware linting

- **Object-level:** TE's `BestPracticeAnalyzer_IgnoreRules` annotation is honored, so existing suppressions carry over. Config-level suppressions by rule ID and object glob are also supported, each with a mandatory `reason`.
- **Diff-aware mode** (`--base <ref>`): only *new* violations block. Violations that already existed are reported, collapsed, so brownfield models can adopt CI linting without a big-bang cleanup. "Same violation" means same rule plus the same object as matched by the diff engine, so a renamed object doesn't create a "new" violation.

---

## 10. Semantic diff algorithm

The diff engine compares **object graphs**, never text lines. Text diffs appear only *inside* a single changed expression, to show what changed.

### 10.1 Materialize

For each side: resolve the ref to a commit, list the model's blobs (`git ls-tree -r -z <commit> -- <modelPath>`), read them in one `git cat-file --batch` process, and deserialize in memory (§5.1). `WORKTREE` reads files from disk. Folder sides load directly.

### 10.2 Build snapshots

Both sides become `ModelSnapshot`s (§5.3). Relationships are keyed by **endpoints** (`FromTable[FromColumn] -> ToTable[ToColumn]`) rather than by name, because PBIP relationship names are GUIDs that tools may regenerate.

### 10.3 Match objects (per object type, within the parent scope)

1. **Exact key** match (same name under the same parent). **Measures** match **model-wide** by name, because measure names are unique in a model, so moving a measure between tables shows up as a `moved` change.
2. **Lineage tag**: an unmatched base object and an unmatched head object with the same `lineageTag` → **rename** (confidence `certain`). Desktop keeps lineage tags across renames.
3. **Heuristic rename** for objects still unmatched (same type, same parent; tables match model-wide). Score each base × head pair:
   - *Measures and calculated columns and items:* normalized DAX token sequence equal → 0.95. Otherwise token-level similarity (Levenshtein ratio over tokens) × 0.7 + matching properties (format string, display folder, data type) × 0.3.
   - *Data columns:* same `sourceColumn` + same data type → 0.9.
   - *Tables:* Jaccard similarity of `(column name, data type)` sets × 0.7 + partition source similarity × 0.3.
   - Assign greedily by best score above the threshold (default **0.8**, configurable). The result is a `likelyRename` with confidence `high` (≥ 0.9) or `medium`. The comparison is capped at 200 × 200 per type and scope, so cost stays bounded.
4. Everything left over is `added` or `removed`.

### 10.4 Compare properties

A **property catalog** lists, per object type, the semantic properties to compare and the change kind each produces. Noise is ignored by default: `lineageTag`, `sourceLineageTag`, `changedProperty`, and annotations matching a configurable ignore list (layout and `PBI_*` housekeeping annotations, with exceptions such as `PBI_FormatHint`). Collections (columns in a hierarchy, members in a perspective, translations) are compared as sets or ordered lists, as appropriate.

**Expressions (DAX and M)** are compared in two steps. First, if the token sequences are equal after removing whitespace and comments, the change is `formattingOnly` (severity `info`, hidden unless `includeFormattingOnly`). Otherwise the change is a real expression change, and its details include a line-level text diff of that one expression.

### 10.5 Severity policy (defaults, overridable per change kind in config)

| Severity | Meaning | Examples (default classification) |
|---|---|---|
| 🔴 `breaking` | Likely to break consumers or change security | Table, column, measure, hierarchy, calculation item or UDF **removed or renamed** (thin reports and other models bind by name); measure **moved** to another table (report field references include the table); **RLS** role filter added, changed or removed; role removed; `modelPermission` changed; OLS permission changed; data type changed on a relationship key column. |
| 🟠 `high` | Changes results | Measure, calculated column or table, or calculation item expression changed; relationship **cross-filter direction → both**; **cardinality** changed; `isActive` changed; security filtering behavior changed; relationship removed; column **data type** changed; partition source or mode changed; shared expression or M parameter value changed; calculation group precedence or item ordinal changed. |
| 🟡 `medium` | Changes defaults or behavior in reports | Column **summarize-by** changed; sort-by column changed; `isKey` changed; relationship added; format string *expression* changed; `discourageImplicitMeasures` changed; compatibility level changed; table or column hidden. |
| ⚪ `low` | Cosmetic but visible | Format string, display folder, description, data category, translations, perspectives; objects unhidden. |
| ▫️ `info` | No semantic effect | Formatting-only expression changes; ignored-annotation changes; objects added (listed so reviewers see them). |

The policy is a table in code, documented in `docs/diff-severities.md` (M3), and every change kind has a fixture test.

### 10.6 Change record

```json
{
  "id": "measure.expression:Sales/[Total Sales]",
  "kind": "measure.expressionChanged",
  "severity": "high",
  "object": { "type": "measure", "table": "Sales", "name": "Total Sales", "key": "table:Sales/measure:Total Sales" },
  "rename": null,
  "before": "SUM ( Sales[Amount] )",
  "after": "SUMX ( Sales, Sales[Quantity] * Sales[Unit Price] )",
  "details": { "formattingOnly": false, "expressionDiff": "@@ -1 +1 @@\n-SUM ( Sales[Amount] )\n+SUMX ( Sales, Sales[Quantity] * Sales[Unit Price] )" },
  "impact": { "downstream": ["table:Sales/measure:Sales Ratio", "table:Sales/measure:Sales YoY %"], "reports": ["report:reports/Sales Overview.Report"] },
  "location": { "file": "Sales.SemanticModel/definition/tables/Sales.tmdl", "line": 12 }
}
```

A rename looks like `"rename": { "from": "Revenue", "to": "Total Sales", "confidence": "certain|high|medium", "evidence": "lineageTag|expression|columns" }`. Severity follows the policy (renames are `breaking`), and the record states the confidence, so reviewers can tell a definite rename from a guess. Per-object snippets for before and after can use `TmdlSerializer.SerializeObject(obj, qualify)` (verified).

### 10.7 Markdown output (PR comment)

```markdown
<!-- tabulartools:review -->
### Semantic model review — `Sales.SemanticModel`  ·  base `a1b2c3d` → head `e4f5a6b`
🔴 2 breaking · 🟠 3 high · 🟡 1 medium · ⚪ 4 low  — 3 reports in 2 repos use this model

#### 🔴 Breaking
| Object | Change | Impact |
|---|---|---|
| `Sales[Revenue]` → `Sales[Total Sales]` | measure renamed (certain: lineage tag) | 2 measures, 3 reports |
| Role `West Region` · `Sales` | RLS filter changed `[Region] = "West"` → `[Region] IN {"West","North"}` | security |
<details><summary>🟠 High (3)</summary> … </details>
<details><summary>Lint: 1 new violation (12 existing, not blocking)</summary> … </details>
<details><summary>Measure tests: 14 passed · 1 expected change · 0 regressions</summary> … </details>
```

---

## 11. Measure tests and the test file format

### 11.1 The problem

A PR changes TMDL files. Evaluating a changed measure needs a model *with data* that contains the new definition. But v1 never writes over XMLA, so the PR version can't be deployed. And on Linux CI there is no local engine.

### 11.2 Approach: query-scoped overrides against a read-only live model

DAX queries can **redefine measures for the duration of one query** (`DEFINE MEASURE Sales[Total Sales] = <expr>`), and other measures that reference an overridden measure use the query's definition. Query-scoped columns (`DEFINE COLUMN`) and tables (`DEFINE TABLE`) work the same way. This gives **regression testing without deployment**:

- **Differential mode (default).** For each test case, run the same query twice against the deployed model (a dev or test workspace, read-only):
  - **base run:** `DEFINE` the base-ref definitions of every measure or calculated column that differs between base and head;
  - **head run:** `DEFINE` the head-ref definitions of the same set.

  Both runs happen at the same moment on the same data, so data refreshes don't cause false failures. And because both sides override the same set, drift between the deployed model and the base branch cancels out for the objects under test. **No values are stored in Git.**
- **Snapshot mode (opt-in).** Expected values are stored in the test file and compared to a live run, optionally with head overrides applied. This is useful for pinned business facts ("FY2023 revenue is 1,234,567.00"). It needs pinned filters (closed periods) so refreshes don't move the numbers.
- **Structural changes** (relationships, data types, partitions, new tables) **can't be simulated in a query**. They're reported as `structuralChangesNotTested`. The run doesn't pretend to cover them. (Testing them needs a deployed head model, which is out of scope for v1.)
- **Expected change vs regression.** In differential mode, a value change on a measure the PR edited *directly* is an *expected change*: reported with before and after values, non-blocking by default (blocking with `--strict`). A change on any *other* measure (a dependent, or one included only as a guard) is a **regression** and fails the test.

**(To verify in M4 against a live engine:** that model measures referencing an overridden measure pick up the query definition, and how `DEFINE COLUMN` behaves on the service. Both are believed to work as described; the M4 notes will record the result.)

### 11.3 Query shape

```dax
DEFINE
    MEASURE Sales[Total Sales] = SUMX ( Sales, Sales[Quantity] * Sales[Unit Price] )   -- override (head side)
EVALUATE
CALCULATETABLE (
    SUMMARIZECOLUMNS (
        'Date'[Year], Sales[Region],
        "Total Sales", [Total Sales],
        "Sales Ratio", [Sales Ratio]
    ),
    'Date'[Year] IN { 2023, 2024 }          -- pinned filters from defaults + case
)
ORDER BY 'Date'[Year], Sales[Region]
```

Results are compared by group key, never by row position. Numbers use `|a − b| ≤ max(absolute, relative × |b|)`. BLANK maps to `null`. Errors become `{ "error": "<message>" }` and are compared as values (an error appearing or disappearing counts as a change).

### 11.4 Test file format (`*.measuretests.yaml`)

```yaml
# yaml-language-server: $schema=../schemas/measure-tests.schema.json
schemaVersion: 1
model: ../Sales.SemanticModel          # relative to this file; source of head/base definitions
profile: dev                           # connection profile name (tabulartools.yaml); never a connection string
defaults:
  tolerance: { absolute: 0.005, relative: 1.0e-9 }
  filters:
    - "'Date'[Year] IN { 2023, 2024 }"  # pin closed periods
cases:
  - id: sales-by-year-region
    description: Core sales measures by year and region
    measures: ["[Total Sales]", "[Sales Ratio]", "[Margin %]"]
    groupBy: ["'Date'[Year]", "Sales[Region]"]
    filters: []
    asRole: null                       # e.g. "West Region" to evaluate under RLS
    assertions:
      - type: differential             # default when assertions is omitted
      - type: invariant                # must hold on every row
        expr: "[Margin %] >= -1 && [Margin %] <= 1"
  - id: fy2023-total
    measures: ["[Total Sales]"]
    filters: ["'Date'[Year] = 2023"]
    assertions:
      - type: snapshot
        capturedAt: "2026-10-27T23:00:00Z"
        columns: ["[Total Sales]"]
        rows:
          - [1234567.00]
```

Notes:

- Snapshot rows are **columnar** (`columns` + positional `rows`, one row per line), which keeps Git diffs readable.
- `invariant` assertions become a query-scoped measure that counts violating rows. They are cheap property tests that need no stored data.
- **Data sensitivity:** snapshot values are real aggregates from the model. Teams decide whether those may live in Git. Differential mode exists partly so they don't have to.

### 11.5 Executors

`IDaxExecutor.ExecuteAsync(query, options) → DaxResult`, implemented by:

| Executor | Where | Use |
|---|---|---|
| `XmlaDaxExecutor` | `Live` | ADOMD.NET against `powerbi://` or `asazure://` with an Entra ID token. Also `localhost:<port>` for Power BI Desktop **on Windows only** (verified: Linux throws `NotSupportedException`). |
| `RecordedDaxExecutor` | `Live` | **Offline mock for CI.** Replays results from a recordings file keyed by SHA-256 of the canonicalized query + target. Unknown queries fail loudly. Fixture recordings are hand-written for the synthetic model. `--record` on the live executor captures new recordings. |

Power BI Desktop's port is discovered from `%LOCALAPPDATA%\Microsoft\Power BI Desktop\AnalysisServicesWorkspaces\*\Data\msmdsrv.port.txt` (Windows only). This gives a fast local loop while the PBIP is open in Desktop.

---

## 12. Lineage and the lineage config format

### 12.1 Principles

- **Everything is explicit.** The config says which repos to scan and where models, reports and database projects live. **Source objects are found by parsing** partition and shared-expression M (and native SQL), **never by naming conventions**.
- **Every edge carries evidence** (file, object, and the expression fragment that produced it).
- **What can't be resolved is reported, never guessed.** Dynamic sources, unknown connectors and unparseable SQL show up in `unresolved` with the raw expression.
- Nothing assumes an organization's layout. Fixtures use synthetic repos (§17).

### 12.2 Config format (`tabulartools.lineage.yaml`)

```yaml
# yaml-language-server: $schema=https://example.invalid/schemas/lineage-config.schema.json
schemaVersion: 1
cacheDir: .tabulartools/repos            # where remote repos are cloned (git-ignored)

repos:
  - id: models
    path: ../example-models              # local checkout (preferred if present)
    url: https://dev.azure.com/example-org/Analytics/_git/example-models   # fallback: cloned with your git credentials
    ref: main
    semanticModels: { include: ["**/*.SemanticModel"], exclude: ["**/archive/**"] }
    reports:        { include: ["**/*.Report"] }

  - id: reports
    path: ../example-reports
    ref: main
    reports: { include: ["reports/**/*.Report"] }

  - id: warehouse                         # optional: SQL database projects that define source objects
    path: ../example-warehouse
    databaseProjects:
      - include: ["src/SalesDW/**/*.sql"]
        database: { server: sales-dw, name: SalesDW }   # logical names (see sources.aliases)

# Service-bound ("byConnection") reports → the model that backs them
publishedModels:
  - match: { workspace: "Example Sales", model: "Sales" }         # from Data Source / Initial Catalog
    semanticModel: { repo: models, path: "Sales/Sales.SemanticModel" }
  - match: { modelId: "00000000-0000-0000-0000-000000000000" }    # from semanticmodelid=…
    semanticModel: { repo: models, path: "Finance/Finance.SemanticModel" }

sources:
  aliases:                                 # map physical server/db names to logical ones
    - logical: { server: sales-dw, database: SalesDW }
      physical:
        - { server: "sql-prod.example.invalid", database: "SalesDW" }
        - { server: "sql-dev.example.invalid",  database: "SalesDW_Dev" }
  resolveParameters: true                  # substitute M parameter defaults (shared expressions)
  expandViews: true                        # follow views to base tables via databaseProjects
  connectors: []                           # optional extra connector descriptors (see 12.3)
```

### 12.3 Extracting source references from models

| Partition / expression kind | How sources are found |
|---|---|
| M partition (`MPartitionSource`) and shared expressions | Parse the M. Follow `let` bindings, including references to other shared expressions and parameters. Recognize connector calls through the **connector catalog**, e.g. `Sql.Database(server, db[, [Query=…]])`, `Sql.Databases(server)`, plus navigation steps `{[Schema=…,Item=…]}[Data]`, `{[Name=…,Kind=…]}[Data]`. `Value.NativeQuery(src, sql)` and `Query=` options send the SQL to the SQL extractor. Strings are constant-folded (`&`), and parameters are substituted from shared expressions marked `IsParameterQuery=true`. |
| Legacy query partition (`QueryPartitionSource`) | The data source's connection string gives server and database; the SQL text goes to the SQL extractor. |
| Entity partition (`EntityPartitionSource`, Direct Lake / dataflows) | Entity name + schema, resolved through its `ExpressionSource` shared expression to the endpoint and database. |
| Calculated table / calculated partition | DAX dependencies give model-internal lineage (table → table). |

**The connector catalog is data-driven**: function name → argument roles (`server`, `database`, `catalog`, `path`) → expected navigation keys. It ships with SQL Server / Azure SQL / Fabric SQL endpoint, plus generic entries for common relational connectors. File and web connectors (`Excel.Workbook(File.Contents(…))`, `Csv.Document`, `SharePoint.Files`, `Web.Contents`) produce `file`/`web` source nodes. Teams can add connectors in config without code changes.

A source reference is `{ connector, server, database, schema, object, kind: table|view|tableOrView|query|file|web, confidence: high|medium|low, evidence }`. Whether something is a table or a view can't be told from M alone, so it stays `tableOrView` unless a database project settles it. When `expandViews` is on, ScriptDom parses `CREATE VIEW` bodies to add view → table edges.

### 12.4 Output graph

```json
{
  "schemaVersion": 1,
  "nodes": [
    { "id": "report:reports/reports/Sales Overview.Report", "type": "report", "repo": "reports", "path": "reports/Sales Overview.Report" },
    { "id": "model:models/Sales/Sales.SemanticModel", "type": "semanticModel", "repo": "models", "name": "Sales" },
    { "id": "table:models/Sales/Sales.SemanticModel/Sales", "type": "table", "name": "Sales" },
    { "id": "source:sales-dw/SalesDW/dbo.FactSales", "type": "sourceObject", "kind": "table", "server": "sales-dw", "database": "SalesDW", "schema": "dbo", "object": "FactSales" }
  ],
  "edges": [
    { "from": "report:…", "to": "model:…", "type": "usesModel", "evidence": { "file": "reports/Sales Overview.Report/definition.pbir", "detail": "byConnection: workspace 'Example Sales', model 'Sales'" } },
    { "from": "model:…", "to": "table:…", "type": "hasTable" },
    { "from": "table:…", "to": "source:…", "type": "loadsFrom", "evidence": { "file": "Sales/Sales.SemanticModel/definition/tables/Sales.tmdl", "object": "partition Sales", "detail": "Source{[Schema=\"dbo\",Item=\"FactSales\"]}[Data]" }, "confidence": "high" }
  ],
  "unresolved": [
    { "at": "table:models/Finance/Finance.SemanticModel/FX Rates", "reason": "dynamic source: server computed from a function call", "expression": "…" }
  ]
}
```

Renderers: JSON, Markdown (tree plus an "affected reports" list for PR comments), and Mermaid (`graph LR`) for docs and wikis.

**Field-level lineage** (report visual → measure → column) would need PBIR visual parsing, which is a v1 non-goal. The graph schema leaves room to add `usesField` edges later.

---

## 13. Live access and the auth model

### 13.1 Scope

Live access is used only by `run_dax` and measure tests (and later by reading a model over XMLA). Everything through lint, diff and lineage is offline.

### 13.2 Token acquisition

We **acquire tokens ourselves** and pass them to ADOMD through `AdomdConnection.AccessToken` (`Microsoft.AnalysisServices.AccessToken`), refreshing through `OnAccessTokenExpired`. Both members exist on Linux (verified). Why:

- ADOMD's built-in interactive login **isn't available on Linux**. The spike got *"When interactive authentication is not supported, an external access-token is required"* (verified).
- One credential model across Windows, Linux, CI and MCP.
- Tokens are only ever sent to allowlisted hosts (§14).

`ITokenProvider` uses an Azure.Identity credential chain for scope `https://analysis.windows.net/powerbi/api/.default`. The mode is selected per profile:

| Mode | Credential | Typical use |
|---|---|---|
| `azureCli` | `AzureCliCredential` | **ADO pipelines** with a workload-identity-federation service connection (run inside the `AzureCLI@2` task). **No secrets stored anywhere.** Also handy for local dev after `az login`. |
| `servicePrincipal` | `EnvironmentCredential` (`AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, plus `AZURE_CLIENT_SECRET` or `AZURE_CLIENT_CERTIFICATE_PATH`) | Pipelines that use a secret or certificate from a variable group or Key Vault. |
| `interactive` | `InteractiveBrowserCredential` (Windows: WAM broker) | Local dev on Windows. |
| `deviceCode` | `DeviceCodeCredential` | Headless or Linux local dev. |
| `default` | Ordered chain: environment → workload identity → Azure CLI → interactive (Windows) / device code (other OS) | Default. |

Token cache: MSAL's persistent cache (DPAPI on Windows, keyring on Linux, opt-in). **Secrets are never read from committed files.** Profiles name an auth *mode* and, at most, *names* of environment variables.

### 13.3 Permissions needed (documented for users)

- XMLA read enabled on the capacity (Premium, PPU or Fabric).
- The identity needs Build (or higher) permission on the semantic model to run DAX queries. Evaluating as an RLS role (`asRole`, connection-string `Roles=`) needs model write or admin rights.
- A service principal must be allowed by the tenant's developer settings and added to the workspace.
- Posting PR comments: the project's Build Service identity needs **Contribute to pull requests** on the repo.

### 13.4 Linux status

On Linux, connecting with a service principal reached Entra ID and got a real AADSTS error for the fake app ID. With an external token, ADOMD made an HTTPS call to `api.powerbi.com`, which this sandbox's proxy blocked. **The managed XMLA-over-HTTPS path very likely works on Linux, but isn't verified end to end** (no tenant, and the host is blocked). The production target is Windows anyway. M4 ships a manual verification script for the owner to run on Windows against a synthetic test model in their own environment.

---

## 14. Safety model

See [ADR-0008](adr/0008-read-only-safety-model.md).

### 14.1 Read-only by default

The MCP server starts read-only: write tools are **not registered** unless `--allow-writes` is passed. The CLI writes only when a command's purpose is to produce a file (`test gen`, `--out`).

### 14.2 No XMLA writes in v1

- `Live` exposes only `IDaxExecutor` (queries) and, later, `IXmlaModelReader` (reads). There is no code path to `Database.Update`, `Model.SaveChanges`, `AdomdCommand.ExecuteNonQuery`, or TMSL/XMLA command execution.
- **DAX query guard:** the XMLA endpoint accepts TMSL commands as command text, and identities often have write rights. So `run_dax` and the test runner lex the query and accept only queries that start with `DEFINE` / `EVALUATE`, or `SELECT … FROM $SYSTEM.*` DMVs. Everything else is rejected before a connection opens.
- **Future deploy:** if added, it lives in a separate assembly behind an `IModelDeployer` interface, with an explicit `--allow-deploy` flag, a per-profile `allowDeploy: true`, and an ADR. Nothing in v1 needs to change to add it.

### 14.3 Writes go only to local files

- All writes go through `Writes.FileWriter`. It confines paths to the workspace roots (no `..` escapes or symlink escapes), writes atomically (temp file + rename), and preserves the existing line endings and encoding.
- Write tools default to **dry run** and return a `WriteResult`: `{ dryRun, files: [{ path, action: created|modified|deleted, unifiedDiff }] }`. Every write response lists exactly which files changed.
- TMDL edits are **targeted** (replace one object's block in its file), never a full re-serialization. The spike showed `SerializeDatabaseToFolder` renames files from object names (`roles/West.tmdl` → `roles/West Region.tmdl`), which would cause noisy, unrelated Git changes.
- v1 write tools: `generate_measure_tests` only. Candidates after M5: `rename_object` (rename plus fix every DAX reference, using the dependency graph), `apply_lint_fixes` (BPA `FixExpression`), `format_dax`.

### 14.4 Untrusted content

Model metadata (descriptions, expressions, annotations), PR text and test files are **data, not instructions**. Tool outputs are structured so they're clearly data, and the `review_pr` prompt says so. Write tools are off by default and sandboxed, so an injected instruction can't silently modify files.

### 14.5 Data exposure

`run_dax` returns model data to the LLM context. Mitigations:

- a row cap (default 500);
- `--disable-live`, and a config option `live.allowRunDax: false` for teams that allow measure tests but not ad-hoc queries;
- a host allowlist: tokens and queries go only to `*.powerbi.com`, `*.asazure.windows.net`, `localhost` (Windows), and hosts listed in config;
- differential tests by default, so aggregates don't need to be stored in Git.

---

## 15. Platform support

| Capability | Windows | Linux | macOS | Evidence |
|---|---|---|---|---|
| Load PBIP / TMDL / TMSL, in memory from Git blobs | ✅ | ✅ | ✅ (expected) | Verified on Linux in M0 |
| Lint, diff, lineage, test file handling, rendering | ✅ | ✅ | ✅ (expected) | Pure managed code; tested on Linux and Windows in CI from M1 |
| Git operations (git CLI) | ✅ | ✅ | ✅ | git on PATH required |
| XMLA queries to `powerbi://` with our token | ✅ (to verify on owner's machine) | ⚠️ likely; verified up to the HTTPS call | ⚠️ untested | §13.4 |
| ADOMD built-in interactive login | ✅ | ❌ (verified) | ❌ | Not used; we supply tokens |
| Power BI Desktop local engine / SSAS over TCP (`localhost:port`) | ✅ | ❌ (verified: `NotSupportedException`) | ❌ | — |
| Interactive browser login (WAM) | ✅ | device code instead | device code instead | Azure.Identity |
| ADO PR comment posting | ✅ | ✅ | ✅ | REST over HTTPS (unverified until run against ADO) |

**Builds:** `net10.0`, `AnyCPU`. CI runs build and tests on `ubuntu-latest` and `windows-latest` (§17.3). Release artifacts: NuGet tool packages (CLI and MCP), plus self-contained single-file executables for `win-x64` and `linux-x64`.

---

## 16. Configuration files

| File | Committed? | Contents |
|---|---|---|
| `tabulartools.yaml` (repo root of the *user's* repo) | Yes | Lint (rulesets, severity overrides, suppressions with reasons, `failOn`), diff (severity overrides, ignored annotations, rename threshold), tests (test file globs, default profile, tolerances), lineage config path, and connection **profiles: endpoint + database + auth mode only**. |
| `tabulartools.local.yaml` | **No** (git-ignored) | Per-developer overrides, e.g. a personal dev workspace profile. |
| `tabulartools.lineage.yaml` | Yes | §12.2 |
| `*.measuretests.yaml` | Yes | §11.4 |
| Environment variables | — | `TABULARTOOLS_*` overrides (e.g. `TABULARTOOLS_PROFILE`), `AZURE_*` for credentials, `SYSTEM_ACCESSTOKEN` for ADO. |

```yaml
# tabulartools.yaml (example — synthetic)
schemaVersion: 1
profiles:
  dev:
    endpoint: powerbi://api.powerbi.com/v1.0/myorg/Example%20Sales%20Dev
    database: Sales
    auth: default            # default | azureCli | servicePrincipal | interactive | deviceCode
lint:
  rulesets: [builtin, microsoft-bpa, ./rules/team-rules.json]
  failOn: error
  severity: { AVOID_FLOATING_POINT_DATA_TYPES: warning }
  suppress:
    - { rule: OBJECTS_WITH_NO_DESCRIPTION, objects: "table:Staging*/**", reason: "staging tables are hidden helpers" }
diff:
  failOn: none
  renameThreshold: 0.8
tests:
  files: ["tests/**/*.measuretests.yaml"]
  profile: dev
lineage:
  config: tabulartools.lineage.yaml
```

Precedence: CLI arguments > environment variables > `tabulartools.local.yaml` > `tabulartools.yaml` > built-in defaults.

---

## 17. Fixtures, testing and CI

### 17.1 Synthetic fixtures (no employer data, ever)

- **`fixtures/pbip/ExampleSales/`** — a small, realistic PBIP sales star schema. `Sales` fact; `Date`, `Product`, `Customer`, `Store` dimensions; about 10 measures; one calculation group; one RLS role; M partitions against `sql.example.invalid` through parameters and a native query. **Deliberate anti-patterns** (each mapped to the rule that should catch it):
  - floating-point `Amount` column;
  - a bidirectional relationship;
  - `/` instead of `DIVIDE`;
  - an unqualified column reference in a measure;
  - summarize-by `sum` on a key column;
  - a hidden, unused column;
  - a measure with no format string;
  - `IFERROR`;
  - a calculated column using `RELATED`;
  - a hard-coded server literal;
  - a date table that isn't marked as a date table.
- **`fixtures/diff/ExampleSales-v1/` and `-v2/`** — two snapshots. At test time a helper builds a **temporary Git repo with two commits** from them, using the git CLI (no nested `.git` committed). v2 includes:
  - a measure expression change and a formatting-only change;
  - relationship → both directions, and a cardinality change;
  - a column data type change and a summarize-by change;
  - an RLS filter change;
  - a measure rename *with* a lineage tag, a column rename *without* one (heuristic), and a measure moved between tables;
  - an added table and a removed measure.
- **`fixtures/lineage/`** — two synthetic repos, materialized as temp Git repos at test time:
  - `example-models`: two semantic models with parameters, a native query, and an unresolvable dynamic source, plus one `byPath` report;
  - `example-reports`: thin reports bound `byConnection`.

  An optional third `example-warehouse` SQL project (tables and views) is added in M5 if view expansion ships.
- **Recordings** for `RecordedDaxExecutor`, hand-written for the synthetic model.

Naming rules for fixtures: `example-*` names, `*.example.invalid` hosts (RFC 2606 reserved), all-zero GUIDs, invented data.

### 17.2 Test layers

| Layer | What |
|---|---|
| Unit | DAX lexer and resolver, M parser, SQL extractor, severity policy, YAML round trips, dialect adapter. |
| Fixture / golden | Lint violations, diff change lists, and lineage graphs against checked-in expected JSON and Markdown (golden files, updated with an explicit `UPDATE_GOLDEN=1`). |
| Compatibility harness | All 71 Microsoft BPA rules compile and run; produces the coverage table. |
| Host | In-process MCP client calls each tool and checks schemas and annotations. CLI commands are checked for exit codes and formats. |
| Live (opt-in) | Skipped unless `TABULARTOOLS_LIVE_TESTS=1` and a profile are set. Never runs in public CI. |

### 17.3 CI for this repo

This repo is hosted on GitHub, so its own CI uses GitHub Actions with an `ubuntu-latest` + `windows-latest` matrix: restore, build with warnings as errors, test, and pack. The **user-facing** CI deliverable is the sample ADO pipeline (M3), sketched here:

```yaml
# samples/azure-pipelines/pr-review.yml (sketch)
trigger: none
pr: { branches: { include: [main] } }
pool: { vmImage: windows-latest }
steps:
  - checkout: self
    fetchDepth: 0                       # need the target branch for merge-base
  - task: UseDotNet@2
    inputs: { version: 10.x }
  - script: dotnet tool install -g TabularTools.Cli
  - script: >
      tabulartools review --base origin/$(System.PullRequest.TargetBranchName)
      --head $(System.PullRequest.SourceCommitId) --format markdown --out $(Build.ArtifactStagingDirectory)/review.md
    displayName: Review semantic model changes
  - script: tabulartools ado comment --file $(Build.ArtifactStagingDirectory)/review.md --marker review
    condition: always()
    env: { SYSTEM_ACCESSTOKEN: $(System.AccessToken) }
```

---

## 18. Risks

| # | Risk | Likelihood / impact | Mitigation |
|---|---|---|---|
| R1 | **BPA dialect compatibility.** TE's Dynamic LINQ dialect and wrapper object model are larger than what we emulate, so some rules won't evaluate. | High / Medium | `TeCompatView` + dialect adapter; harness over all 71 Microsoft rules; unsupported rules listed in `skippedRules`, never silent; coverage table published in M2 notes. |
| R2 | **TOM lags new TMDL features.** Desktop ships monthly; a newer PBIP may use keywords our TOM version doesn't know. | Medium / High | Keep `Microsoft.AnalysisServices` current (Dependabot); clear `TmdlFormatException` diagnostics with file and line; refresh fixtures regularly. |
| R3 | **Query-scoped override semantics** (dependents pick up overrides; `DEFINE COLUMN` on the service) unverified. | Medium / High (M4 depends on it) | Verify early in M4 on a live engine; if columns don't work, limit differential mode to measures and report column changes as structural. |
| R4 | **Live XMLA unverified** (no tenant; `api.powerbi.com` blocked in the build environment). | Medium / Medium | Interfaces plus `RecordedDaxExecutor`; manual verification script for the owner; marked unverified in milestone notes. |
| R5 | **Data drift** breaks snapshot tests. | High (for snapshot mode) / Low | Differential mode is the default; pinned filters; tolerances. |
| R6 | **Sensitive data** in test files or LLM context. | Medium / High | Differential default; row caps; `--disable-live`; docs. |
| R7 | **M parsing coverage**: custom functions, dynamic sources, unusual connectors. | High / Medium | Tolerant parser; data-driven connector catalog; `unresolved` with evidence instead of guesses. |
| R8 | **Rename heuristics** wrong (false or missed renames). | Medium / Medium | Lineage tag first; confidence labels; configurable threshold; heuristics never trigger automatic actions. |
| R9 | **Git in CI**: shallow clones, detached HEAD, PR merge commits. | High / Medium | Documented `fetchDepth: 0`; detect a shallow repo or missing base and fail with exit code 4 and a fix hint; diff `merge-base(target, source)`. |
| R10 | **Licensing of dependencies.** `Microsoft.AnalysisServices` and `AdomdClient` are under Microsoft's proprietary client-library license. The license URL couldn't be fetched in M0. NuGet restore is standard, but **self-contained builds and tool packages bundle the DLLs**. | Medium / High (for distribution) | Confirm redistribution terms before the first release (Tabular Editor 2 and DAX Studio redistribute them, which is precedent, not proof). Tracked in ADR-0010. |
| R11 | **Third-party rule licensing.** TE's community rules have no license. | Certain / Low | Not vendored; loaded from a user path. Microsoft's rules (MIT) vendored with notice. |
| R12 | **MCP spec / SDK churn** (SDK 2.x; the spec still evolves). | Medium / Low | Thin host layer; host round-trip tests; Core independent of MCP. |
| R13 | **Trademarks.** "Copilot", "Power BI" and "Tabular Editor" are trademarks. | Medium / Medium (for OSS and commercial) | Don't use "Copilot" in the product name; use "for Power BI" descriptively; check the final name (§19). |
| R14 | **Large models** (hundreds of tables, thousands of measures). | Low / Medium | Lazy DAX and M analysis; content-hash cache; bounded rename matching; pagination in tools. |
| R15 | **ADO comment limits and permissions.** | Medium / Low | Truncation with an artifact link; documented Build Service permission; upsert by marker so comments don't pile up. |
| R16 | **.NET support window.** | Certain / Low | Target .NET 10 LTS (ADR-0002). |
| R17 | **Prompt injection** through model metadata or PR content. | Medium / Medium | Read-only default; sandboxed writes; outputs presented as data; prompt guidance (§14.4). |

---

## 19. Open questions for review

1. ~~License~~ — decided: **MIT** ([ADR-0010](adr/0010-license-mit-vs-apache.md)).
2. **Product and CLI name.** The repo is `TabularTools`; the NuGet ID `TabularTools` is free. The brief's working title includes "Copilot", a Microsoft trademark; I recommend not using it. Proposed: product "TabularTools", CLI `tabulartools`, packages `TabularTools.Cli` / `TabularTools.Mcp`. OK?
3. **.NET 10 instead of .NET 8** (ADR-0002) — OK?
4. **Severity defaults** (§10.5). In particular: is a column rename `breaking` by default, and is `summarizeBy` `medium`?
5. **Differential mode as the default** for measure tests, with "expected change" non-blocking unless `--strict` (§11.2) — OK?
6. **`get_dependencies` as a new tool**, and renaming `diff_pbip` → `diff_model` and `gen_measure_tests` → `generate_measure_tests` — OK?
7. **Lineage scope.** Is resolving source objects into SQL database projects (view → table expansion) wanted in M5, or is "source object as named in M" enough for v1?
8. **ADO specifics** to keep in mind for the sample pipeline: workload identity federation (`azureCli` mode) vs a service principal secret?

---

## 20. Milestone plan

Each milestone ships tests, an updated README section, and `docs/milestones/Mx.md` (what was built, decisions, known gaps, unverified items).

| Milestone | Scope | Exit criteria |
|---|---|---|
| **M0** | This document, ADRs, repo basics. | Owner review. |
| **M1 — Skeleton** | Solution layout (§4.1); `Core` model loading + `ModelSnapshot`; MCP host with `open_model`, `describe_model` (+ a trivial `ping`); CLI `describe`; `ExampleSales` PBIP fixture; GitHub Actions matrix (Linux + Windows); JSON Schemas generated. | Both hosts describe the fixture on Linux and Windows CI. |
| **M2 — Lint** | DAX lexer and dependency graph; `get_dependencies`; lint engine (BPA JSON + dialect adapter + `TeCompatView` + built-in rules); vendored Microsoft rules + notices; suppressions; diff-aware mode stubbed until M3 (needs the matcher); `lint_model` + CLI `lint`. | Harness coverage table; every fixture anti-pattern caught; exit codes correct. |
| **M3 — Semantic diff** | Git reader; matcher (key → lineage tag → heuristic); property catalog; severity policy; impact; Markdown renderer; `diff_model` + CLI `diff`/`review`; diff-aware lint enabled; `ado comment` (mocked); sample ADO pipeline YAML. | Every v1→v2 fixture change detected with the expected severity; golden Markdown. |
| **M4 — Measure tests** | Test file format + schema; planner (overrides, invariants); comparator; `XmlaDaxExecutor` + `RecordedDaxExecutor`; token providers; DAX guard; `run_dax`, `generate_measure_tests`, `run_measure_tests` + CLI; Windows verification script. | Offline tests pass with recordings; live parts marked unverified unless the owner runs the script. |
| **M5 — Lineage** | M parser + connector catalog; SQL extractor; PBIR `datasetReference` reader; config + repo materialization; graph + renderers; `resolve_lineage` + CLI `lineage`; lineage impact added to `review`. | Two-repo fixture resolves end to end; unresolved cases reported with evidence. |
