# ADR-0010: License — MIT vs Apache-2.0

- **Status:** Accepted — **MIT** (owner decision, 2026-10-27)
- **Date:** 2026-10-27

## Context

The project should be (1) useful now, (2) releasable as open source, and (3) open to a commercial offering later. What matters most here: patent protection, commercial-friendliness, and compatibility with the third-party material we include or depend on.

### What we include or depend on

| Component | License | How we use it |
|---|---|---|
| Microsoft BPA rules (`microsoft/Analysis-Services`) | MIT, © 2016 Microsoft | **Vendored** JSON file, with notice |
| Tabular Editor 2 TOMWrapper semantics | MIT, © Tabular Editor ApS | Logic **ported** (with attribution) |
| Tabular Editor community rules | **No license** (all rights reserved) | **Not vendored**; user-supplied path only |
| `ModelContextProtocol` (MCP C# SDK) | Apache-2.0 | NuGet dependency |
| `System.Linq.Dynamic.Core` | Apache-2.0 | NuGet dependency |
| ScriptDom, YamlDotNet, Azure.Identity, MSAL, System.CommandLine | MIT | NuGet dependencies |
| `Microsoft.AnalysisServices`, `…AdomdClient` | **Microsoft proprietary client-library license** | NuGet dependency; **bundled** in self-contained builds and tool packages |

## Comparison

| Topic | MIT | Apache-2.0 |
|---|---|---|
| **Patent grant** | None explicit. Courts may imply one, but nothing is written down. | **Explicit patent license** from every contributor for their contributions, plus **patent retaliation**: anyone who sues over patents in the work loses their license. |
| **Commercial-friendliness** | Very permissive. Anyone, including you, can build proprietary or commercial products on it. Minimal obligations (keep the copyright and license notice). | Equally permissive for commercial use. The patent terms make it the more common choice for corporate users and contributors, because legal teams like the explicit grant. Slightly more obligations: keep the `NOTICE` file and mark modified files. |
| **Your commercial path (open core / paid add-ons)** | Works. Your own code can be relicensed freely. Contributors' code stays MIT. | Works the same way. Contributors' code stays Apache-2.0 and comes with a patent grant, which makes building a commercial product on community contributions cleaner. |
| **Including the MIT BPA rules and TE2-derived logic** | Compatible. Keep their notices. | Compatible. MIT code can be included in an Apache-2.0 project, keeping its notices (list them in `THIRD_PARTY_NOTICES.md`). |
| **Depending on Apache-2.0 libraries** (MCP SDK, Dynamic LINQ) | Compatible. Distribute their license and `NOTICE` text with binaries. | Compatible, same license. |
| **Microsoft proprietary AS client libraries** | **Unaffected by this choice.** Redistribution of those DLLs in our packages is governed by Microsoft's terms, which must be confirmed separately (the license URL couldn't be fetched from the build environment). | Same. |
| **GPLv2-only compatibility** | Compatible | Not compatible with GPLv2-only (compatible with GPLv3). Unlikely to matter here. |
| **Ecosystem norms** | .NET runtime, Tabular Editor 2, Microsoft's MCP server repo | The MCP C# SDK, many Microsoft and Apache-ecosystem projects |
| **Length / readability** | ~170 words | ~10 pages, plus a `NOTICE` convention |

## Things that apply either way

- **Contributions:** use a **DCO** (`Signed-off-by`) at minimum. If you might later relicense *contributed* code under commercial terms, you'd need a **CLA** instead; a DCO alone doesn't allow that. Under either license, a common alternative is to keep commercial features in a separate, proprietary repo and leave the OSS core under its license.
- **Trademark:** neither license grants trademark rights (Apache-2.0 says so explicitly). Choose a name you can protect, and avoid "Copilot" (Microsoft trademark).
- **Confirm the Microsoft AS client-library redistribution terms** before the first release that bundles them (self-contained builds, `dotnet tool` packages).

## Decision

**MIT**, chosen by the owner on 2026-10-27.

For the record, the comparison above said: both licenses fully support goals 1–3. **Apache-2.0** fits goal 3 slightly better because of the explicit patent grant and retaliation clause, at the cost of a longer license and a `NOTICE` file. **MIT** is simplest and matches Tabular Editor 2 and the .NET runtime. Neither choice affects the BPA rules or our dependencies.

## Consequences

- `LICENSE` (MIT) is committed; project files set `<PackageLicenseExpression>MIT</PackageLicenseExpression>`.
- `THIRD_PARTY_NOTICES.md` lists bundled or vendored third-party material (Apache-2.0 dependencies' notices, the Microsoft BPA rules when vendored in M2, TE2-derived logic).
- Contributions use a DCO (`Signed-off-by`). If contributed code ever needs relicensing for a commercial edition, a CLA must be introduced first.
- Still open: confirm redistribution terms of the Microsoft Analysis Services client libraries before the first release.
