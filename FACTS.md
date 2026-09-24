# MMCA.Common — Canonical Facts

**Single source of truth for the framework-wide facts that otherwise drift across dozens of docs.**
_As of: 2026-09-23 (framework v1.209.0) — **generated from source by `build/facts`; do not hand-edit the numbers below.**_

> **Rule: link here, don't restate.** Other docs (scorecards, CLAUDE.md files, READMEs, the LinkedIn/Medium
> campaigns) must **reference** these facts rather than copy the numbers inline. A "thirteen packages"
> count or a "~68 fitness methods" figure typed into another file is drift waiting to happen — point at
> this file. The ADR table and its count/range are owned by the published ADR index
> (<https://ivanball.github.io/docs/adr/>, source `docs-src/adr/README.md` in the Website repo). Per-repo
> facts (test totals, scorecard indices) live in that repo's published scorecard, **not** here.

## Framework version
- **Current: `v1.209.0`** (MinVer-derived from the git tag at `main` HEAD).
- All consumers (**MMCA.ADC**, **MMCA.Store**, MMCA.Helpdesk) track this version in **lockstep** — every
  `MMCA.Common.*` entry in each consumer's `Directory.Packages.props` is bumped together (ADR-016; no phased
  rollout).

## Published packages — **22**
Released in lockstep to nuget.org and GitHub Packages (dual-registry, ADR-053; the packable projects under `Source/` carrying a `<PackageId>`):

1. `MMCA.Common.AI`
2. `MMCA.Common.AI.Anthropic`
3. `MMCA.Common.AI.OpenAI`
4. `MMCA.Common.Application`
5. `MMCA.Common.Domain`
6. `MMCA.Common.Infrastructure`
7. `MMCA.Common.Shared`
8. `MMCA.Common.API`
9. `MMCA.Common.Grpc`
10. `MMCA.Common.UI`
11. `MMCA.Common.UI.Maui`
12. `MMCA.Common.UI.Web`
13. `MMCA.Common.AI.Testing`
14. `MMCA.Common.Aspire`
15. `MMCA.Common.Aspire.Hosting`
16. `MMCA.Common.Gateway`
17. `MMCA.Common.Testing`
18. `MMCA.Common.Testing.Architecture`
19. `MMCA.Common.Testing.Aspire`
20. `MMCA.Common.Testing.E2E`
21. `MMCA.Common.Testing.UI`
22. `MMCA.Common`

## Architecture Decision Records
The ADRs live in the Website repo (`docs-src/adr/`), published at
<https://ivanball.github.io/docs/adr/>. The **canonical index is that repo's `docs-src/adr/README.md`**:
it owns the range/count and the one-line summaries. Do not restate the `(001-NNN)` range elsewhere.

## Architecture fitness functions
- **140 test methods across 55 abstract `*TestsBase` classes**, shipped once in the
  `MMCA.Common.Testing.Architecture` package (ADR-015) and re-run as thin subclasses across all consuming
  repos (Common, ADC, Store).
- MMCA.Common's own build executes **292** of them (the methods of the bases its arch-tests
  subclass, plus its Common-only direct tests, e.g. `FrameworkSanityTests`/`SpecificationFitnessTests`).

## Governance rubric
- The 34-category evaluation rubric is canonical in the Website repo
  (`docs-src/governance/ArchitectureEvaluationCriteria.md`, published at
  <https://ivanball.github.io/docs/governance/>). Each repo's published scorecard is scored against it.

## Where the rest lives (don't duplicate)
- Per-repo scores/indices and test counts → that repo's published scorecard
  (<https://ivanball.github.io/docs/governance/>).
- Remediation status → that repo's published backlog (same location); cross-repo themes →
  workspace `Docs/Architecture/ArchitectureRemediation.md` (rollup, links only).
- Release notes / security model → `CHANGELOG.md` / `SECURITY.md` (this repo); versioning policy /
  FinOps → the published guides (<https://ivanball.github.io/docs/guides/>).

## Keeping this current
This file is **generated from source** and **gated in CI** (the `facts` job runs `--check` and fails the
build if the committed file drifts from source). Regenerate it at each framework release with:

```bash
dotnet run --project build/facts -- .
```

The figures are computed directly: version from the git tag, package count from packable `Source/*`
projects, and the fitness counts from `MMCA.Common.Testing.Architecture`.
The workspace `Tools/invtool -- facts ./MMCA.Common` shares this same generator and produces an identical file.