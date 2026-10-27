# TabularTools

**Semantic-model tools for AI agents and CI pipelines, for Power BI PBIP/TMDL projects in Git.**

TabularTools is an MCP server and a CLI built on the same .NET engine. It gives agents (Claude Code, Claude Desktop, VS Code) and Azure DevOps pipelines tools that go beyond basic model editing:

- **Read** a semantic model from PBIP/TMDL (or `model.bim`), including straight from Git history.
- **Lint** it with Best Practice Analyzer–compatible rules, plus built-in and team rules.
- **Semantically diff** two commits: changed DAX, relationship direction and cardinality, data types, RLS filters, renames, each with a severity.
- **Run measure regression tests** against a live model without deploying the PR version.
- **Resolve lineage** across repos: report → semantic model → source tables and views.

> **Status: M1 — skeleton.** Models can be opened and described from both the MCP server and the CLI. Lint, diff, measure tests and lineage come in M2–M5. The design is in the [architecture document](docs/ARCHITECTURE.md) and the [decision records](docs/adr/README.md).

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

```bash
tabulartools describe path/to/Project.pbip --detail summary
```

`describe` accepts a `.pbip` file, a `*.SemanticModel` folder, a TMDL `definition` folder, or a `model.bim` file (default: the current folder).

| Option | Meaning |
|---|---|
| `--detail summary\|objects\|full` | Counts and tables / objects and properties / plus DAX and M expressions and RLS filters |
| `--tables`, `--types`, `--name`, `--exclude-hidden` | Filter by table, object type, name glob, visibility |
| `--format text\|markdown\|json` | Default: text in a terminal, JSON when output is redirected |
| `--out <file>` | Write to a file |

Exit codes: `0` success, `1` blocking findings (from M2), `2` usage error or path that isn't a model, `3` invalid TMDL/TMSL (reported with file and line), `4` environment error.

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

The server is read-only. `--root` (repeatable) sets the folder relative paths resolve against. `--allow-writes` and `--disable-live` are accepted for later milestones and change nothing yet.

### Build and test

```bash
dotnet build
```

```bash
dotnet test
```

Tests run on Linux and Windows in CI against the synthetic fixture in `fixtures/pbip/ExampleSales`.

## Design documents

| Document | What it covers |
|---|---|
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | Components, tool schemas, data flow, diff algorithm, test file and lineage config formats, auth, platform support, risks |
| [docs/adr/](docs/adr/README.md) | One record per significant decision (including the open license question) |
| [docs/milestones/](docs/milestones/) | What each milestone built, decisions made, known gaps |

## Roadmap

| Milestone | Scope | Status |
|---|---|---|
| M0 | Architecture document + ADRs | Done |
| M1 | Skeleton: solution, MCP + CLI hosts, `open_model`/`describe_model`, Linux + Windows CI | **Done** — [notes](docs/milestones/M1.md) |
| M2 | Lint engine (BPA JSON rules + built-in rules) | Not started |
| M3 | Semantic diff + PR-comment Markdown + sample ADO pipeline | Not started |
| M4 | Measure regression tests + `run_dax` (live, with an offline mock) | Not started |
| M5 | Cross-repo lineage with an explicit config | Not started |

## Principles

- **Read-only by default.** No writes over XMLA in v1. Local file writes are opt-in, sandboxed, and always report the files they change.
- **Works offline.** Everything except live DAX queries runs without network access, on Windows and Linux.
- **Nothing organization-specific.** Every example and fixture is synthetic.

## License

[MIT](LICENSE). See [ADR-0010](docs/adr/0010-license-mit-vs-apache.md) for the reasoning and [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for bundled third-party material.

*Power BI is a trademark of Microsoft. This project is not affiliated with Microsoft or Tabular Editor ApS.*
