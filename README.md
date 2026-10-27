# TabularTools

**Semantic-model tools for AI agents and CI pipelines, for Power BI PBIP/TMDL projects in Git.**

TabularTools is an MCP server and a CLI built on the same .NET engine. It gives agents (Claude Code, Claude Desktop, VS Code) and Azure DevOps pipelines tools that go beyond basic model editing:

- **Read** a semantic model from PBIP/TMDL (or `model.bim`), including straight from Git history.
- **Lint** it with Best Practice Analyzer–compatible rules, plus built-in and team rules.
- **Semantically diff** two commits: changed DAX, relationship direction and cardinality, data types, RLS filters, renames, each with a severity.
- **Run measure regression tests** against a live model without deploying the PR version.
- **Resolve lineage** across repos: report → semantic model → source tables and views.

> **Status: M0 — design only.** There is no code yet. Start with the [architecture document](docs/ARCHITECTURE.md) and the [decision records](docs/adr/README.md).

## Design documents

| Document | What it covers |
|---|---|
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | Components, tool schemas, data flow, diff algorithm, test file and lineage config formats, auth, platform support, risks |
| [docs/adr/](docs/adr/README.md) | One record per significant decision (including the open license question) |
| [docs/milestones/](docs/milestones/) | What each milestone built, decisions made, known gaps |

## Roadmap

| Milestone | Scope | Status |
|---|---|---|
| M0 | Architecture document + ADRs | **In review** |
| M1 | Skeleton: solution, MCP + CLI hosts, `open_model`/`describe_model`, Linux + Windows CI | Not started |
| M2 | Lint engine (BPA JSON rules + built-in rules) | Not started |
| M3 | Semantic diff + PR-comment Markdown + sample ADO pipeline | Not started |
| M4 | Measure regression tests + `run_dax` (live, with an offline mock) | Not started |
| M5 | Cross-repo lineage with an explicit config | Not started |

## Principles

- **Read-only by default.** No writes over XMLA in v1. Local file writes are opt-in, sandboxed, and always report the files they change.
- **Works offline.** Everything except live DAX queries runs without network access, on Windows and Linux.
- **Nothing organization-specific.** Every example and fixture is synthetic.

## License

Not chosen yet. See [ADR-0010](docs/adr/0010-license-mit-vs-apache.md).

*Power BI is a trademark of Microsoft. This project is not affiliated with Microsoft or Tabular Editor ApS.*
