# Third-party notices

TabularTools is licensed under the [MIT License](LICENSE). It uses or includes the third-party material below. Each package is restored from NuGet under its own license; binary distributions (tool packages, self-contained builds) bundle these assemblies and carry this file.

| Component | License | Use |
|---|---|---|
| [Microsoft.AnalysisServices](https://www.nuget.org/packages/Microsoft.AnalysisServices) (TOM, TMDL) | Microsoft software license terms (proprietary) | NuGet dependency. **Redistribution terms to be confirmed before the first binary release** (see ADR-0010). |
| [ModelContextProtocol](https://www.nuget.org/packages/ModelContextProtocol) (MCP C# SDK) | Apache-2.0 | NuGet dependency (MCP host) |
| [System.CommandLine](https://www.nuget.org/packages/System.CommandLine) | MIT | NuGet dependency (CLI host) |
| [System.Linq.Dynamic.Core](https://www.nuget.org/packages/System.Linq.Dynamic.Core) | Apache-2.0 | NuGet dependency (evaluates BPA rule expressions) |
| [YamlDotNet](https://www.nuget.org/packages/YamlDotNet) | MIT | NuGet dependency (reads `tabulartools.yaml`) |
| [Microsoft Best Practice Analyzer rules](rules/microsoft-bpa/SOURCE.md) (`microsoft/Analysis-Services`) | MIT, Copyright (c) 2016 Microsoft — [license](rules/microsoft-bpa/LICENSE) | **Vendored** unchanged in `rules/microsoft-bpa/` and embedded in the engine |
| [Tabular Editor 2](https://github.com/TabularEditor/TabularEditor) object model | MIT, Copyright (c) Tabular Editor ApS | The lint compatibility view (`src/TabularTools.Core/Lint/Compat`) mirrors Tabular Editor's member names and semantics so BPA rules run unchanged. No Tabular Editor code is included. |
| [Microsoft.Extensions.Hosting](https://www.nuget.org/packages/Microsoft.Extensions.Hosting) | MIT | NuGet dependency (MCP host) |

Later milestones add entries here when they add dependencies or vendored material.

Tabular Editor's community rules (`TabularEditor/BestPracticeRules`) are **not** included: that repository has no license. Point a `lint.rulesets` entry at your own copy to use them.

The Apache-2.0 license text is available at <https://www.apache.org/licenses/LICENSE-2.0>.
