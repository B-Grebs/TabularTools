# ADR-0005: BPA-compatible lint rules; vendor Microsoft's rules, load others by path

- **Status:** Proposed
- **Date:** 2026-10-27

## Context

There is no ruleset in use today; the Microsoft / Tabular Editor default BPA rules were used in the past. License check (M0):

- `microsoft/Analysis-Services` `BestPracticeRules/BPARules.json`: **MIT, © 2016 Microsoft**. 71 rules.
- `TabularEditor/BestPracticeRules`: **no license file** in the repo, so all rights are reserved by default. 29 rules in `BPARules-standard.json`.
- Tabular Editor 2 itself: MIT.

BPA expressions are Dynamic LINQ over Tabular Editor's wrapper object model (`DependsOn`, `ReferencedBy`, `UsedInRelationships`, `Tokenize()`, `outerIt`, `current`, …), not raw TOM.

## Decision

- **Rule format = BPA JSON, accepted verbatim**, plus an optional `x-tabulartools` extension object (severity override, tags, help URL). Teams write their own rules in the same format.
- **Vendor Microsoft's rules** with their MIT notice in `THIRD_PARTY_NOTICES.md`, updated by a script that records the upstream commit.
- **Don't vendor Tabular Editor's community rules.** Users reference their own copy by path or URL.
- Evaluate with `System.Linq.Dynamic.Core` through a **dialect adapter** and a read-only **`TeCompatView`**. Its semantics are ported from TE2's TOMWrapper (MIT, attributed).
- Add built-in C# rules (`TT0xx`) for checks that need the dependency or relationship graph.
- Unsupported rules are reported in `skippedRules`, **never silently passed**. A harness test runs all 71 Microsoft rules.
- Honor TE's `BestPracticeAnalyzer_IgnoreRules` annotation for suppressions.

## Consequences

- Existing BPA users can bring their rule files and suppressions unchanged.
- Full dialect fidelity is unlikely at first. The coverage table in the M2 notes makes the gap visible (target ≥ 90% of Microsoft rules).
- Rules relying on VertiPaq statistics annotations can't fire offline; this is documented.

## Alternatives considered

- **Our own rule language (e.g. YAML + C#-only rules):** cleaner, but throws away the ecosystem teams already know. Rejected as the primary format; built-in C# rules cover what BPA can't.
- **Plugin assemblies for custom rules:** loading arbitrary code into an MCP server is a security concern. Deferred.
