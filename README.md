# TabularTools

**Semantic-model tools for AI agents and CI pipelines, for Power BI PBIP/TMDL projects in Git.**

TabularTools is an MCP server and a CLI built on the same .NET engine. It gives agents (Claude Code, Claude Desktop, VS Code) and Azure DevOps pipelines tools that go beyond basic model editing:

- **Read** a semantic model from PBIP/TMDL (or `model.bim`), including straight from Git history.
- **Lint** it with Best Practice Analyzer–compatible rules, plus built-in and team rules.
- **Semantically diff** two commits: changed DAX, relationship direction and cardinality, data types, RLS filters, renames, each with a severity.
- **Run measure regression tests** against a live model without deploying the PR version.
- **Resolve lineage** across repos: report → semantic model → source tables and views.

> **Status: M4 — measure tests.** Models can be opened, described, linted, dependency-traced and semantically diffed between git refs, and measure regression tests run against a live model (or offline recordings) without deploying, from both the MCP server and the CLI. A sample Azure DevOps pipeline posts the review as a PR comment. Lineage comes in M5. The design is in the [architecture document](docs/ARCHITECTURE.md) and the [decision records](docs/adr/README.md).

## Getting started

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download). Packages aren't published to NuGet yet, so build them locally:

```bash
dotnet pack -c Release -o artifacts
```

```bash
dotnet tool install -g TabularTools.Cli --add-source artifacts
```

```bash
dotnet tool install -g TabularTools.Mcp --add-source artifacts
```

### CLI

Every model command accepts a `.pbip` file, a `*.SemanticModel` folder, a TMDL `definition` folder, or a `model.bim` file (default: the current folder). Every command takes `--format text|markdown|json` (default: text in a terminal, JSON when redirected) and `--out <file>`.

```bash
tabulartools describe path/to/Project.pbip --detail summary
```

```bash
tabulartools lint path/to/Project.pbip --format markdown
```

```bash
tabulartools deps "Sales[Amount]" --model path/to/Project.pbip --direction downstream --depth 0
```

```bash
tabulartools diff --base origin/main --format markdown
```

```bash
tabulartools review --base origin/main --format markdown --out review.md
```

```bash
tabulartools test run tests/sales.measuretests.yaml --base origin/main
```

```bash
tabulartools dax --profile dev --query "EVALUATE SUMMARIZECOLUMNS('Date'[Year], \"Sales\", [Total Sales])"
```

| Command | What it does |
|---|---|
| `describe` | List tables, columns, measures, relationships, roles and more. `--detail summary\|objects\|full`, filters `--tables`, `--types`, `--name`, `--exclude-hidden` |
| `lint` | Check best-practice rules. `--rules`, `--config`, `--fail-on error\|warning\|info\|none`, `--min-severity`, `--rule`, `--base <ref>` (only violations new since that ref block) |
| `deps <object>` | What an object depends on and what depends on it. `--direction upstream\|downstream\|both`, `--depth` (0 = all) |
| `diff` | Semantic diff between git refs (`--base`, `--head`, default `HEAD`; `WORKTREE` for uncommitted files) or two model folders. Compares with the merge base like a PR does (`--no-merge-base` to turn off). `--model`, `--fail-on breaking\|high\|medium\|low\|info\|none`, `--min-severity`, `--include-formatting`, `--marker` |
| `review` | `diff` plus lint violations introduced since the base, plus the measure tests listed in `tests.files`, as one report; `--format markdown` is the PR comment. `--fail-on`, `--lint-fail-on`, `--rules`, `--no-lint`, `--no-tests`, `--profile`, `--strict`, `--json-out` |
| `test run [files]` | Run measure tests (default: the files in `tests.files`). `--base` (default `HEAD`), `--head` (default `WORKTREE`), `--profile`, `--mode differential\|snapshot`, `--case`, `--strict`, `--record <file>` |
| `test gen <file>` | Add a case to a test file. `--model`, `--measures`, `--group-by`, `--filter`, `--mode differential\|snapshot`, `--profile`, `--case-id`, `--role`, `--dry-run` |
| `dax` | Run a read-only DAX query or `$SYSTEM` DMV select through a profile. `--query` or `--file`, `--max-rows`, `--role`, `--timeout` |
| `ado comment --file <md>` | Create or update a PR comment thread in an Azure Pipelines PR build (found again by `--marker`). Does nothing outside PR builds |
| `rules list` | The rules `lint` would run |
| `rules check <file>` | Check every rule in a BPA JSON file can be evaluated (useful in CI for team rule files) |

Exit codes: `0` success, `1` blocking findings (lint violations or diff changes at the `--fail-on` level; failing or erroring measure tests, or changed ones with `--strict`; a rule file with rules that can't run), `2` usage or config error, a path that isn't a model, or a query that's rejected or fails, `3` invalid TMDL/TMSL (reported with file and line), `4` environment error (git missing, unknown ref, shallow clone, sign-in or connection to the live model failed, Azure DevOps call failed).

### Lint rules

`lint` runs two rulesets by default:

- **`builtin`** — five TabularTools rules that need the dependency or relationship graph: broken DAX references, ambiguous filter paths, RLS filter mistakes, hard-coded data sources, measure/column name collisions. See [docs/rules/builtin.md](docs/rules/builtin.md).
- **`microsoft-bpa`** — Microsoft's 71 [Best Practice Analyzer rules](rules/microsoft-bpa/SOURCE.md) (MIT), evaluated with Tabular Editor-compatible semantics. All 71 run.

Add your team's rules as a BPA JSON file in Tabular Editor's format. To use [Tabular Editor's community rules](https://github.com/TabularEditor/BestPracticeRules), download them and reference your copy; they aren't bundled because that repo has no license. Existing `BestPracticeAnalyzer_IgnoreRules` annotations are honored.

Configure lint in a `tabulartools.yaml` next to (or above) your models. A git-ignored `tabulartools.local.yaml` beside it overrides it for one developer.

```yaml
schemaVersion: 1
lint:
  rulesets: [builtin, microsoft-bpa, ./rules/team-rules.json]
  failOn: error                  # error | warning | info | none
  severity:
    AVOID_FLOATING_POINT_DATA_TYPES: warning
    INTEGER_FORMATTING: off      # off disables a rule
  suppress:
    - rule: OBJECTS_WITH_NO_DESCRIPTION
      objects: "table:Staging*/**"   # glob on object keys; ** crosses segments
      reason: staging tables are hidden helpers
```

### Semantic diff

`diff` matches objects between two versions — by name, then by lineage tag (so renames are recognized), then by similarity — and compares their semantic properties. Each change has a kind, a severity, the objects affected downstream, and the TMDL file and line:

| Severity | Examples |
|---|---|
| 🔴 breaking | object removed or renamed, measure moved to another table, RLS filter or role changed |
| 🟠 high | DAX changed, relationship direction/cardinality/active changed, data type changed, partition source changed |
| 🟡 medium | summarize-by, sort-by or key column changed, object hidden, relationship added |
| ⚪ low | format string, display folder, description |
| ▫️ info | objects added; whitespace-only expression changes (hidden unless `--include-formatting`) |

Every change kind and its default severity is listed in [docs/diff-severities.md](docs/diff-severities.md). Override severities and choose when the diff blocks in `tabulartools.yaml`:

```yaml
diff:
  failOn: breaking             # default: none (informational)
  renameThreshold: 0.8         # similarity needed for a heuristic rename
  severity:
    column.summarizeByChanged: low
```

Git is read with the `git` CLI; nothing is checked out or fetched. In CI, check out with full history so the base branch and merge base are present.

### Measure tests

A pull request changes TMDL files, but the version it proposes isn't deployed anywhere. Measure tests evaluate it anyway: each test case runs as a DAX query against a deployed dev or test model (read-only), with the changed measures redefined for that one query (`DEFINE MEASURE …`). In **differential** mode (the default) every case runs twice at the same moment on the same data, once with the base definitions and once with the head definitions, and the results are compared row by row:

- a change on a measure the PR edited directly is **expected** (`changed`, not blocking unless `--strict`);
- a change on any other measure, such as one that depends on the edited one, is a **regression** (`fail`);
- changes a query can't simulate (relationships, data types, partitions, RLS, calculated tables) are listed as **not tested**.

No values are stored in Git. **Snapshot** assertions (stored values, for pinned facts such as a closed year) and **invariants** (a DAX condition that must hold on every row) are also available.

```yaml
# tests/sales.measuretests.yaml
schemaVersion: 1
model: ../Sales.SemanticModel
defaults:
  filters: ["'Date'[Year] IN { 2023, 2024 }"]   # pin closed periods
cases:
  - id: sales-by-year-region
    measures: ["[Total Sales]", "[Margin %]"]
    groupBy: ["'Date'[Year]", "'Store'[Region]"]
    assertions:
      - type: differential
      - type: invariant
        expr: "[Margin %] >= -1 && [Margin %] <= 1"
```

Create cases with `tabulartools test gen` or the `generate_measure_tests` tool. The JSON Schema is [`schemas/measure-tests.schema.json`](schemas/measure-tests.schema.json).

Connections are named **profiles** in `tabulartools.yaml`: an endpoint, a database and an auth mode, never a secret. Tokens come from Azure.Identity: `azureCli` (pipelines with a workload identity federation service connection, or after `az login`), `servicePrincipal` (the `AZURE_*` variables), `interactive`, `deviceCode`, `token` (an environment variable), or `default` (environment → workload identity → Azure CLI → browser on Windows). A profile can instead replay a **recordings** file, so tests run offline in CI; `test run --record <file>` captures one from a live run.

```yaml
profiles:
  dev:
    endpoint: powerbi://api.powerbi.com/v1.0/myorg/Sales%20Dev
    database: Sales
    auth: azureCli
  recorded:
    recordings: tests/sales.recordings.yaml
tests:
  files: ["tests/**/*.measuretests.yaml"]
  profile: dev
live:
  allowRunDax: true             # false turns off ad-hoc queries (dax, run_dax); tests still run
```

Queries only go to `*.powerbi.com`, `*.asazure.windows.net` and hosts listed in `live.allowedHosts`, and only DAX queries and `$SYSTEM` DMV selects are sent: TMSL and XMLA commands are rejected before connecting. Local Power BI Desktop (`endpoint: desktop`) works on Windows only.

**Live access hasn't been verified against a real tenant yet.** [`scripts/verify-live.ps1`](scripts/verify-live.ps1) checks it against the synthetic [LiveCheck model](fixtures/live/README.md), including whether model measures pick up query-scoped overrides.

### Azure DevOps

[`samples/azure-pipelines/pr-review.yml`](samples/azure-pipelines/pr-review.yml) runs `review` on every pull request (with measure tests when a service connection is set), publishes the report as an artifact, posts it as one PR comment that is updated on each push, and fails the build on blocking findings. Setup notes are in [samples/azure-pipelines/README.md](samples/azure-pipelines/README.md).

### MCP server

Register the server in your MCP client. For Claude Code:

```bash
claude mcp add tabulartools -- tabulartools-mcp --root /path/to/your/repo
```

Generic `mcp.json` / Claude Desktop entry:

```json
{
  "mcpServers": {
    "tabulartools": { "command": "tabulartools-mcp", "args": ["--root", "/path/to/your/repo"] }
  }
}
```

| Tool | What it does |
|---|---|
| `ping` | Server and TOM versions, roots, whether writes are enabled |
| `open_model` | Load a model; returns a `modelId` handle and a summary. Invalid TMDL is reported with file and line |
| `describe_model` | List tables, columns, measures, relationships, roles, partitions and more, with filters, three detail levels and paging |
| `get_dependencies` | What an object depends on and what depends on it, across all DAX plus relationships, sort-by and hierarchies |
| `lint_model` | Best-practice violations with rule, object, severity and fix hint; settings from `tabulartools.yaml`. `baseRef` marks which violations are new |
| `diff_model` | Semantic diff between git refs, against the working tree (`head: "WORKTREE"`), or between folders; optional PR-comment Markdown |
| `run_measure_tests` | Run measure tests: base vs head (default `HEAD` vs the working tree), snapshots and invariants, with expected changes, regressions and untested structural changes |
| `generate_measure_tests` | Add a test case to a test file; previews by default, saves with `save: true` when the server allows writes |
| `run_dax` | Run a read-only DAX query or `$SYSTEM` DMV select through a profile; rows capped |

Resources: `tabulartools://rulesets`, `tabulartools://rulesets/{id}` (`builtin`, `microsoft-bpa`), `tabulartools://diff/change-kinds`, `tabulartools://schemas/measure-tests` and `tabulartools://workspace/test-files`.

The server never writes to a model or a service. `--root` (repeatable) sets the folder relative paths resolve against and the only place files may be written. `--allow-writes` lets `generate_measure_tests` save test files (it only previews without it). `--disable-live` hides `run_dax` and limits measure tests to recordings, so nothing queries a live model.

### Build and test

```bash
dotnet build
```

```bash
dotnet test
```

Tests run on Linux and Windows in CI against the synthetic fixtures in `fixtures/`. Diff tests build a throwaway git repository from `fixtures/diff/ExampleSales-v1` and `-v2`. Golden Markdown files live in `fixtures/diff/expected/`; after an intended output change, regenerate them with `UPDATE_GOLDEN=1 dotnet test`. Measure tests replay hand-written recordings (`fixtures/pbip/ExampleSales/tests/`, `fixtures/diff/tests/`); the same command rewrites their query text when the query planner changes, keeping the values. Live tests are skipped unless `TABULARTOOLS_LIVE_TESTS=1`.

## Design documents

| Document | What it covers |
|---|---|
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | Components, tool schemas, data flow, diff algorithm, test file and lineage config formats, auth, platform support, risks |
| [docs/adr/](docs/adr/README.md) | One record per significant decision (including the open license question) |
| [docs/milestones/](docs/milestones/) | What each milestone built, decisions made, known gaps |
| [docs/diff-severities.md](docs/diff-severities.md) | Every diff change kind and its default severity |

## Roadmap

| Milestone | Scope | Status |
|---|---|---|
| M0 | Architecture document + ADRs | Done |
| M1 | Skeleton: solution, MCP + CLI hosts, `open_model`/`describe_model`, Linux + Windows CI | Done — [notes](docs/milestones/M1.md) |
| M2 | Lint engine (BPA JSON rules + built-in rules), DAX dependencies | Done — [notes](docs/milestones/M2.md) |
| M3 | Semantic diff + PR-comment Markdown + sample ADO pipeline | Done — [notes](docs/milestones/M3.md) |
| M4 | Measure regression tests + `run_dax` (live, with an offline mock) | **Done** (live parts unverified) — [notes](docs/milestones/M4.md) |
| M5 | Cross-repo lineage with an explicit config | Not started |

## Principles

- **Read-only by default.** No writes over XMLA in v1. Local file writes are opt-in, sandboxed, and always report the files they change.
- **Works offline.** Everything except live DAX queries runs without network access, on Windows and Linux.
- **Nothing organization-specific.** Every example and fixture is synthetic.

## License

[MIT](LICENSE). See [ADR-0010](docs/adr/0010-license-mit-vs-apache.md) for the reasoning and [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for bundled third-party material.

*Power BI is a trademark of Microsoft. This project is not affiliated with Microsoft or Tabular Editor ApS.*
