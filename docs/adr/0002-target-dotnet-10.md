# ADR-0002: Target .NET 10 (LTS)

- **Status:** Proposed
- **Date:** 2026-10-27

## Context

The brief says ".NET 8+". .NET 8 LTS support ends on **2026-11-10**, two weeks after this ADR. .NET 10 is the current LTS (supported to November 2028). `Microsoft.AnalysisServices` 19.118.3 and `AdomdClient` ship `net8.0` assemblies (plus `net472` and `net6.0`). The MCP SDK `ModelContextProtocol` 2.3.0 targets net8/net9/net10. During M0, a .NET 10 app loaded TMDL with TOM 19.118.3 on Linux without issues.

## Decision

All projects target **`net10.0`**. Pin the SDK in `global.json` (`10.0.x`, `rollForward: latestFeature`).

## Consequences

- ADO pipelines add a `UseDotNet@2` step for `10.x`. Self-contained single-file builds avoid needing a runtime on agents that only run the tool.
- .NET 10 features become available: `dnx` for running tool packages on demand (handy for MCP client config), and MCP server NuGet packaging.
- Anyone pinned to .NET 8 must use the self-contained build. That's acceptable because the tool is a standalone process, not a library.

## Alternatives considered

- **net8.0:** out of support almost immediately. Rejected.
- **Multi-target net8.0;net10.0:** extra CI and test cost with no clear user. Rejected for now; can be added if a library consumer appears.
