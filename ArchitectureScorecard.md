# MMCA.Common — Architecture Evaluation Scorecard

**Weighted architecture-health index: 80%** (218 of 272 weighted points across 28 applicable categories; 6 N/A categories excluded). Scored against the 34-category rubric at `C:/Projects/MMCA/Docs/Architecture/ArchitectureEvaluationCriteria.md`.

## Executive summary

MMCA.Common is a mature, deliberately-engineered shared framework (eleven NuGet packages spanning DDD, Clean Architecture, CQRS, and Aspire hosting) that scores in the upper band on architecture health. Its defining strength is that the most consequential rules are *not prose* — they are executable fitness functions (NetArchTest layer/purity/extraction suites, all 25 passing) plus a redundant compile-time MSBuild guard, both gated in CI on every PR. This earns clean 4s in Clean Architecture, SOLID, Microservices Readiness, Data Architecture, and Testability, with strong supporting 4s in Design Patterns, Cross-Cutting Concerns, and Code Quality. The biggest risks cluster in two areas: (1) the asynchronous/broker reliability path, where at-least-once delivery is advertised but consumer-side idempotency and retry/backoff are convention-only and a code comment promises a retry policy that is never configured; and (2) governance/lifecycle hygiene for a *published* framework — no consumer-side idempotency seam, a safety-critical MassTransit v8 pin guarded only by a comment (which has already regressed in production), no CHANGELOG/breaking-change policy, and a single low score in Compliance/Privacy (soft-delete-only with no right-to-erasure seam). Front-end quality is solid but unenforced: zero bUnit component tests for a package whose job includes shipping reusable Blazor primitives.

## Scorecard

| # | Category | Part | Weight | Score | Weighted | Key evidence / notes |
|---|----------|------|--------|-------|----------|----------------------|
| 1 | SOLID Principles | A | 3 | 4 | 12 | DIP auto-enforced twice (LayerEnforcement.targets + NetArchTest DomainPurity/LayerDependency); OCP via Scrutor decorators + filter Strategy registry; ISP repository split documented. S107/S1200 deliberately relaxed (review-enforced). |
| 2 | Design Patterns | A | 2 | 4 | 8 | Idiomatic Decorator pipeline, Result, Repository/UoW, Specification, Outbox, per-type Strategy; behavior locked by tests + ADRs. Minor: Expression.Invoke spec composition untested for SQL translation; hand-rolled mediation (deliberate post-MediatR relicensing). |
| 3 | Clean Architecture | A | 3 | 4 | 12 | Inward dependency rule guarded by MSBuild target **and** 25 passing NetArchTest fitness functions; Domain verifiably framework-pure. Blemish: Application transitively pulls MiniProfiler.AspNetCore.Mvc (type-level test misses it). |
| 4 | Domain-Driven Design | A | 3 | 3 | 9 | Clean entity hierarchy, rich value objects, factory→Result, identifier aliases vs primitive obsession. No DDD-specific fitness functions; only one concrete bounded context (Notifications) ships; minor factory-convention inconsistencies. |
| 5 | Vertical Slice Architecture | A | 2 | 3 | 6 | Convention-scanning DI + reflection module discovery enable feature slices; Notifications organized as textbook slices. No slice-cohesion fitness test; deliberate hybrid; some DTOs/validators per-aggregate. |
| 6 | CQRS & Event-Driven | A | 2 | 3 | 6 | Clean command/query split, documented decorator pipeline, exemplary transactional outbox. **Confirmed:** consumer-side idempotency convention-only; broker retry comment without configured policy; no event-schema versioning. |
| 7 | Microservices Readiness | A | 3 | 4 | 12 | Real, tested, CI-enforced extraction seams: DB-per-service, IMessageBus transport abstraction, gRPC contracts, JWKS, trace propagation. Soft spot: async-path resilience unconfigured. |
| 8 | Data Architecture | A | 3 | 4 | 12 | Central audit stamping, DB-per-service routing/isolation, per-source outbox atomicity, cross-source FK degradation, two-real-DB SQLite integration suite. Soft-delete-filter/concurrency-conflict not directly test-asserted; migration drift warning suppressed (delegated to consumer CI). |
| 9 | API & Contract Design | A | 2 | 3 | 6 | RFC 9457 Problem Details unified across HTTP+gRPC, 200-with-error-body guard, standardized pagination/filtering, header versioning. No OpenAPI generation/verification; ServiceContractAttribute's claimed enforcement test does not exist. |
| 10 | Cross-Cutting Concerns | A | 2 | 4 | 8 | Validation/logging/transactions/caching/feature-gating centralized in one Scrutor decorator pipeline, test-pinned; strongly-typed options with ValidateOnStart; no committed secrets. Two divergent resilience stacks (UI Polly v7 vs server v8). |
| 11 | Security | A | 3 | 3 | 9 | JWT algorithm pinning, PBKDF2-SHA512 600k iters + timing-safe compare, AES-256-GCM field encryption, server-side authZ, rate limiting; security analyzers at error severity. No explicit CI vuln-scan gate; insecure dev defaults (requireHttpsMetadata=false); no SECURITY.md/threat model. |
| 12 | Performance & Scalability | A | 2 | 3 | 6 | Async throughout, AsNoTracking/AsSplitQuery/projections, N+1 batch loader, server-side paging, two-tier cache, Redis backplane. No load/benchmark tests in-repo; no perf fitness function; no max-page-size guard. |
| 13 | Observability & Operability | A | 2 | 3 | 6 | OTel logs/metrics/traces (OTLP+Azure Monitor), 3-tier health probes, correlation scopes, trace continuity across outbox, test-enforced poll-noise suppression. **Confirmed:** outbox dead-letter Meter never registered (AddMeter missing) so metric not exported; no RED metrics; operability surface untested. |
| 14 | Testability & Test Strategy | A | 3 | 4 | 12 | ~1432 fast tests in a correct pyramid; dual (runtime + compile-time) dependency enforcement; real-SQLite integration; FakeTimeProvider; shipped reusable test NuGet packages. Coverage wired (coverlet) but never collected/gated. |
| 15 | Best Practices & Code Quality | A | 2 | 4 | 8 | 5 analyzers at error + TreatWarningsAsErrors + AnalysisMode=All; CPM with security/licensing-aware pins; Result pattern, zero throw-new in Domain; every suppression scoped+justified; no TODO debt. No CI vuln-audit step; missing package README; broad analyzer downgrades. |
| 16 | Maintainability & Evolvability | A | 2 | 3 | 6 | Acyclic enforced layers, 11 cohesive packages, MinVer SemVer from tags. **Confirmed:** blanket update reintroduced known-bad MassTransit v9; no CHANGELOG/breaking-change policy; coupling enforced but never measured/trended. |
| 17 | DevOps & Deployment | A | 2 | 3 | 6 | Automated CI gate + tag-triggered idempotent pack/push to GitHub Packages with ephemeral least-priv token; reproducible MinVer versioning. **Confirmed:** security/audit only implicit (no Dependabot/CodeQL/explicit audit); no release notes/rollback; snupkg built but never pushed. |
| 18 | UI Architecture & Component Design | B | 3 | 3 | 9 | Small presentational primitives + service base classes; real composition (RenderFragment/DynamicComponent plugin); SoC auto-enforced (UI→Shared only). No bUnit/render tests; component conventions review-only; markup duplication; Bootstrap+MudBlazor residue. |
| 19 | State Management & Data Flow | B | 3 | 3 | 9 | Explicit owners, URL-as-source-of-truth, unidirectional flow, all per-user state scoped per circuit, zero static mutable user state. **Confirmed:** UnsavedChangesGuard reads stale `IsDirty` [Parameter]; no bUnit tests. |
| 20 | Design System & UI Consistency | B | 2 | 3 | 6 | Centralized MudBlazor theme, real CSS design-token layer, uniform empty/loading/error primitives, exemplary MudDataGrid quirk wrapper. **Confirmed:** mixed Bootstrap chrome in NavMenu; no theme/visual-regression enforcement; light palette only. |
| 21 | Accessibility (a11y) | B | — | **N/A** | — | Excluded from index — framework library, no end-user screens to audit. |
| 22 | Responsive Design & Cross-Browser/Device | B | — | **N/A** | — | Excluded from index — assessable only in consumer apps. |
| 23 | Front-End Performance | B | 2 | 3 | 6 | Server-side paging baked into base class, request cancellation, SSR persistence, debounced scroll, IntersectionObserver infinite scroll. **Confirmed:** unbounded DOM growth in MobileInfiniteScrollList (no virtualization); no client perf measurement. |
| 24 | Forms, Validation & UX Safety | B | — | **N/A** | — | Excluded from index — concrete forms live in consumer apps. |
| 25 | Navigation, Routing & Information Architecture | B | — | **N/A** | — | Excluded from index — app-level concern. |
| 26 | Front-End Security | B | — | **N/A** | — | Excluded from index — assessed under Security (#11) for shared surface. |
| 27 | Internationalization & Localization | B | — | **N/A** | — | Excluded from index — not present in framework. |
| 28 | Front-End Testing & Quality | B | 3 | 2 | 6 | Strong shipped Playwright E2E package + UI service unit tests, **but** zero bUnit component tests and no a11y/visual-regression; E2E not a merge gate here. |
| 29 | Resilience, Reliability & Business Continuity | C | 3 | 3 | 9 | Polly standard handler on all HTTP/gRPC, outbox at-least-once with retry/dead-letter/per-source isolation, health/warmup gating, HTTP idempotency filter. **Confirmed:** broker consumer retry promised in comment but unconfigured; no DR/RTO/RPO/SLOs. |
| 30 | Compliance, Privacy & Data Governance | C | 2 | 1 | 2 | **Lowest score.** Only audit-trail + payload-light logging present. **Confirmed:** soft-delete-only with no erasure/anonymization seam; outbox payloads retained forever; no PII classification/consent/DSR scaffolding. |
| 31 | Cost Efficiency / FinOps | C | 2 | 3 | 6 | Idle outbox poll spans suppressed from export (test-enforced), configurable poll interval, idle-vCPU-aware HTTP keepalive. No sampler exposed; per-message Info logging uncapped; classic FinOps levers N/A (provisions nothing). |
| 32 | Dependency & Supply-Chain Management | C | 3 | 3 | 9 | CPM pins all versions, MinVer SemVer, NuGet audit + TWAE effectively gates vulns (OTel pin proves it acted). **Confirmed:** MassTransit pin guarded only by comment; no lock files/SBOM; no breaking-change policy. |
| 33 | Developer Experience & Inner Loop | C | 2 | 3 | 6 | One-command build/test/pack, fast no-DB suite, gitignored cross-repo local.props override, identical local/CI enforcement. **Confirmed:** hand-maintained 11-package swap list can silently drift; manual GITHUB_TOKEN onboarding. |
| 34 | Architecture Governance & Documentation | C | 2 | 3 | 6 | Fitness functions + compile-time guard + thorough current CLAUDE.md + structured ADRs. **Confirmed:** Docs/Architecture/ArchitecturalAnalysis.md contradicts code on DB-per-service; two biggest recent decisions lack ADRs; no staleness automation. |

**Weighted architecture-health index = 218 / 272 = 80%** (28 applicable categories; categories 21, 22, 24, 25, 26, 27 are N/A and excluded).

## Strengths

The framework's four standout strengths all share one root cause: **architecture rules are executable and CI-gated, not aspirational.**

**Clean Architecture (4, weighted 12).** The inward-pointing dependency rule is guarded by two independent mechanisms — a compile-time MSBuild target (`Source/Build/MMCA.Common.LayerEnforcement.targets`) that fails the build on a forbidden `ProjectReference`, and 25 runtime NetArchTest fitness functions (`Tests/Architecture/MMCA.Common.Architecture.Tests/` — LayerDependencyTests, DomainPurityTests, MicroserviceExtractionTests) that all pass when executed. Domain is verifiably framework-pure (a grep for EF/ASP.NET/serialization attributes returns zero matches), ports live in Application and adapters in Infrastructure. This is the textbook definition of "enforced automatically + documented + evolved."

**SOLID Principles (4, weighted 12).** DIP — the most consequential principle — is enforced at both compile time and runtime, including an Application-purity proxy (`IQueryableExecutor` abstracts EF's `IQueryable` so Application never references EF Core). OCP is realized through the Scrutor `TryDecorate` pipeline and a per-type filter Strategy registry (`QueryFilterService.RegisterStrategy`) that extends without modification; ISP is a documented, deliberate repository split (`IEntityReader`/`IEntityQuerier`/`IReadRepository`/`IWriteRepository`).

**Microservices Readiness (4, weighted 12) & Data Architecture (4, weighted 12).** Extraction seams are real and tested, not slideware: database-per-service (`DataSourceResolver`, `EntityDataSourceRegistry`, `CrossDataSourceDegradeConvention` validated over two real SQLite databases in `MultiSourceSqliteIntegrationTests`), a transport-agnostic `IMessageBus` (InProcess vs MassTransit broker) with `MicroserviceExtractionTests` forbidding transport leakage into Application/Domain/Shared, per-source outbox atomicity, gRPC `.Contracts` proto convention, JWKS cross-service auth, and distributed-trace continuity restored across the async outbox hop. Audit fields are stamped centrally by interceptor and thoroughly test-enforced.

**Testability (4, weighted 12) plus Design Patterns / Cross-Cutting / Code Quality (4 each).** ~1432 fast tests form a correct (non-inverted) pyramid with no Docker/DB dependency; shared test infrastructure ships as consumable NuGet packages (`MMCA.Common.Testing`, `.Testing.E2E`) so downstream apps reuse fixtures and page objects rather than duplicating them. Cross-cutting concerns are centralized in one decorator pipeline with ordering/rollback semantics locked by tests; five analyzers run at error severity under `TreatWarningsAsErrors` with every suppression individually justified.

## Top risks & remediation backlog

Ranked by weighted gap = (4 − score) × weight. Items at the same priority are grouped.

### Priority 6

**#28 Front-End Testing & Quality** — score 2, weight 3, priority **6**.
- *Gap:* Zero bUnit component tests for a package that ships reusable Blazor primitives; no accessibility (axe/Lighthouse) or visual-regression checks; E2E abstract bases ship but never execute in this repo's CI.
- *Confirmed red flag (medium):* No component tests at all for the UI library — `Tests/Presentation/MMCA.Common.UI.Tests/` references only xUnit/Moq/AwesomeAssertions/coverlet (no bUnit) while `Source/Presentation/MMCA.Common.UI/Components/` ships ~12 components with real branching (MobileCardList, MobileInfiniteScrollList). The pyramid has no fast base layer.
- *Confirmed red flag (low):* No axe/Lighthouse or visual-regression step in `ci.yml`; no a11y tooling in `Directory.Packages.props`.
- *Recommended fix:* Add bUnit and write render/parameter/EventCallback tests for the primitives (start with the two list components that carry branching logic), wire `Deque.AxeCore.Playwright` into the existing E2E flows, and run at least one browser journey in MMCA.Common CI so regressions in the shared E2E helpers are caught here.

**#30 Compliance, Privacy & Data Governance** — score 1, weight 2, priority **6** (the single lowest category score).
- *Gap:* No right-to-erasure/anonymization seam, no retention/purge, no PII classification/consent/DSR scaffolding.
- *Confirmed red flag (medium):* Soft-delete is the built-in, documented deletion model (`AuditableBaseEntity.Delete()` sets `IsDeleted=true`; global query filters everywhere; CLAUDE.md "entities are never hard-deleted") with no governance-aware erasure path — the exact GDPR/CCPA conflict the rubric names. Only the low-level `ExecuteDeleteAsync` maintenance primitive partially mitigates, and it bypasses audit/events.
- *Confirmed red flag (low):* Processed outbox rows (serialized event payloads, potential PII) are never purged — `OutboxProcessor` only sets `ProcessedOn`; no cleanup service or TTL exists (ADR-003 itself notes the table "grows until cleaned up").
- *Confirmed red flag (low):* No PII/consent/data-subject machinery anywhere (though `EncryptedStringConverter` AES-256-GCM field encryption is a real, shippable PII control).
- *Recommended fix:* Add an `IAnonymizable`/erasure-orchestration seam that reconciles soft-delete with subject-deletion (anonymize-in-place leaving an audit trail), add an outbox-purge background option with configurable retention, and write an ADR framing the soft-delete-vs-erasure tradeoff and the consumer's data-controller obligations.

### Priority 3 (score 3, weight 3 categories)

**#4 Domain-Driven Design** — priority **3**, no confirmed red flags.
- *Gap:* No DDD-specific fitness functions (references-by-ID, value-object immutability, anemic-model prevention rely on base-class design + review); factory→Result convention has minor inconsistencies (`UserNotification.Create` returns bare entity; `Money.operator+` throws on currency mismatch).
- *Recommended fix:* Add NetArchTest rules asserting aggregates expose private ctors + factory methods and that no cross-aggregate navigation properties exist; normalize the factory convention to always return `Result<T>`.

**#11 Security** — priority **3**, no confirmed red flags.
- *Gap:* No explicit CI dependency-vuln gate or `<auditSources>` nuget.config; no security fitness tests; insecure dev defaults (`requireHttpsMetadata=false`, permissive dev CORS) rely on downstream discipline; no SECURITY.md/threat model.
- *Recommended fix:* Add a `dotnet list package --vulnerable` (or restore `--audit`) gate to CI, add NetArchTest assertions for security invariants (no stray `[AllowAnonymous]`, no AllowAnyOrigin+AllowCredentials), and commit a SECURITY.md with an OWASP Top-10 review note.

**#18 UI Architecture & Component Design** — priority **3**, no confirmed red flags.
- *Gap:* No bUnit/render tests; component-design conventions review-only; no `ShouldRender` overrides; no size/complexity budget for page components.
- *Recommended fix:* Shared with #28 — add bUnit coverage; consider an analyzer/convention check for EditorRequired contracts on shared components.

**#19 State Management & Data Flow** — priority **3**.
- *Confirmed red flag (low):* `UnsavedChangesGuard` exposes `IsDirty` only as a `[Parameter]`; its `HandleBeforeInternalNavigationAsync` reads the parameter which lags one render, so clearing dirty state and calling `NavigateTo` without an intervening `StateHasChanged()` still shows the "unsaved changes" dialog (`Source/Presentation/MMCA.Common.UI/Components/UnsavedChangesGuard.razor`, lines 24, 38-55) — a real, previously-observed production foot-gun, untested.
- *Recommended fix:* Add an optional `Func<bool>?` live-accessor parameter so the guard reads current dirty state at navigation time, and cover it with a bUnit test.

**#29 Resilience, Reliability & Business Continuity** — priority **3**.
- *Confirmed red flag (medium):* `IntegrationEventConsumer` rethrows handler failures with a comment claiming "MassTransit will retry per its configured policy," but `ConfigureBrokerTransport` (`DependencyInjection.cs` lines 403-431) calls only `cfg.ConfigureEndpoints(context)` — **no** `UseMessageRetry`/`UseDelayedRedelivery`. Faulted messages go straight to the `_error` queue with zero retries/backoff, on the extracted-microservice path the framework explicitly markets.
- *Gap:* No backup/restore, RTO/RPO, failover, SLOs, or chaos/fault-injection; resilience config (unlike layer rules) is not test-enforced.
- *Recommended fix:* Add a default `UseMessageRetry` (with backoff+jitter) and `UseDelayedRedelivery` in `ConfigureBrokerTransport`, expose a hook for consumers to tune it, and correct/remove the misleading comment+log message.

**#32 Dependency & Supply-Chain Management** — priority **3**.
- *Confirmed red flag (medium):* The safety-critical MassTransit v8 pin (`Directory.Packages.props` lines 28-36) is guarded only by a prose comment; a blanket "update all" once bumped it to v9.1.2, which crashes every broker-enabled host at startup — a recurring real regression with no automated guard (CI never starts a broker, so the build stays green).
- *Confirmed red flags (low):* No lock files or SBOM for 11 published packages; no documented breaking-change/SemVer policy or CHANGELOG.
- *Recommended fix:* Replace the exact pin with a constrained range `[8.5.5,9.0.0)` (or add a fitness test asserting the MassTransit major stays ≤ 8), enable `RestorePackagesWithLockFile` + commit lock files, add a CycloneDX SBOM step to release, and publish a brief versioning/breaking-change policy.

### Priority 2 (score 3, weight 2 categories)

These ten categories each carry priority **2**. Grouped by theme:

- **#6 CQRS & Event-Driven** — *Confirmed (medium):* no consumer-side idempotency/inbox for at-least-once broker delivery — duplicate side effects possible in any non-idempotent consumer (`IntegrationEventConsumer.cs`; ADR-003 documents the requirement but provides no enforcement). *Confirmed (low):* misleading "MassTransit will retry" comment with no configured policy. **Fix:** ship an optional EF-backed inbox/dedup filter keyed on a message id, and add a unique event Id to base events.
- **#16 Maintainability & Evolvability** — *Confirmed (medium):* blanket NuGet update reintroduced known-bad MassTransit v9 (commit 87d54ee); fix is a comment, not a rule. *Confirmed (low):* no CHANGELOG/breaking-change policy for 11 published packages. **Fix:** as in #32, plus a per-release CHANGELOG.
- **#17 DevOps & Deployment** — *Confirmed (low):* security/audit only implicit (no Dependabot/CodeQL/explicit audit step). **Fix:** add Dependabot + an explicit audit job; push `.snupkg` symbol packages (currently built but never published).
- **#13 Observability & Operability** — *Confirmed (low):* the outbox dead-letter Meter `MMCA.Common.Outbox` is created but no `AddMeter` call exists, so the dead-letter counter is never exported (contradicting CLAUDE.md) — mitigated by an Error-level structured log on the same path. **Fix:** add `AddMeter("MMCA.Common.Outbox")` to `WithMetrics`; emit RED histograms for command/query latency.
- **#9 API & Contract Design** — *Confirmed (low):* `ServiceContractAttribute` documents architecture-test enforcement that does not exist (no test references it). **Fix:** implement the NetArchTest rule (or remove the claim); add OpenAPI generation + a contract snapshot test.
- **#20 Design System & UI Consistency** — *Confirmed (low):* Bootstrap chrome (NavMenu top bar/hamburger) coexists with MudBlazor in the shared package. **Fix:** migrate the remaining Bootstrap chrome to MudBlazor and drop the bundled Bootstrap CSS; source the brand hex from one token.
- **#23 Front-End Performance** — *Confirmed (low):* `MobileInfiniteScrollList` appends every page into one `MudStack` with no virtualization/cap (mobile-only, PageSize 10). **Fix:** add `Virtualize` windowing or a rendered-item cap.
- **#33 Developer Experience & Inner Loop** — *Confirmed (low):* the 11-package local-dev swap list is hand-maintained three times in each consumer's `Directory.Build.targets` and can silently drift. **Fix:** generate the list from a glob, or add a smoke test that the `UseLocalMMCA` swap resolves all packages.
- **#34 Architecture Governance & Documentation** — *Confirmed (low ×2):* `Docs/Architecture/ArchitecturalAnalysis.md` contradicts the code on DB-per-service ("deliberately not database-per-service," race "only mitigated"), and the two biggest recent decisions (DB-per-service, gRPC extraction) lack ADRs. **Fix:** refresh the analysis doc, write the two missing ADRs, and add an ADR index/template.
- **#5 Vertical Slice Architecture** & **#12 Performance & Scalability** & **#31 Cost Efficiency** — no confirmed red flags; gaps are the absence of fitness functions for slice cohesion, the absence of in-repo load/benchmark tests + a max-page-size guard, and the absence of a configurable sampler / log-volume cap respectively. **Fix:** add a slice-cohesion NetArchTest; add a `BenchmarkDotNet` smoke project and a framework-level page-size cap; expose an OTel sampler knob.

## Cross-cutting themes

1. **"Enforced vs. convention-only" is the dividing line between 4s and 3s.** Every category that reached 4 is backed by a fitness function or compile-time guard (layer rules, domain purity, transport coupling, decorator ordering, outbox behavior). Every category capped at 3 has the right design but leaves it to review — DDD invariants, slice cohesion, security invariants, performance rules, UI component conventions, and resilience config all lack an automated guard. The single highest-leverage investment is extending the existing NetArchTest suite to cover these dimensions.

2. **The asynchronous/broker path is the consistent weak seam.** Across CQRS (#6), Microservices (#7), Resilience (#29), and Observability (#13), the synchronous (gRPC/HTTP Polly) path is well-tuned while the broker path lacks configured retry/backoff, consumer idempotency/inbox, event-schema versioning, and an exported dead-letter metric — and a comment over-promises a retry policy that does not exist. This is the most impactful functional cluster to harden.

3. **A safety-critical dependency pin is guarded only by a comment, and it has already failed.** The MassTransit v8 pin recurs as a red flag in #16 and #32 because blanket updates have reintroduced the crashing v9 in production. It exemplifies a broader pattern: important constraints captured as prose (the pin comment, ServiceContractAttribute's claimed test, the "retry policy," CLAUDE.md's dead-letter-metrics claim) rather than as a check.

4. **Published-framework lifecycle hygiene lags the code quality.** For 11 packages consumed downstream there is no CHANGELOG, breaking-change policy, lock files, or SBOM, and the living architecture doc has drifted from the code — strong engineering inside the package, weaker contracts around its evolution.

5. **Front-end is well-architected but under-tested.** UI architecture, state management, design system, and front-end performance all score 3 with sound designs, but the recurring deduction is the same: no bUnit component tests and no UI fitness functions, so the reusable primitives the package ships are validated only at the service layer and via E2E that does not run here.

## Methodology & caveats

- This is an **evidence-based static review** of source, tests, configuration, CI/CD workflows, ADRs, and documentation. No runtime profiling, live broker behavior, deployed-environment inspection, or load testing was performed; claims about runtime behavior (e.g., broker redelivery, metric export, telemetry ingestion) are inferred from code and configuration. The architecture fitness tests were executed (25/25 passing); other behaviors were not exercised at runtime.
- **Scores are authoritative inputs** taken verbatim from the upstream scoring pass; the index (218/272 = 80%) was not recomputed here.
- **Red-flag verdicts** carry a `confirmed`/`adjusted`/`unverified` status; this report surfaces only `confirmed` and `adjusted` (verified-but-reseverity'd) flags in the backlog. Several `unverified` low-severity flags exist in the underlying data and are not escalated here.
- **Thin-evidence categories.** Because MMCA.Common is a framework, not a runnable app, several categories could only be judged on the substrate it provides, not on realized behavior: Performance & Scalability (#12) and Cost/FinOps (#31) have no in-repo load/benchmark evidence; Resilience business-continuity facets (#29) and Data Architecture's SQL-Server-specific paths (#8) are validated only against SQLite/in-memory; and Vertical Slice (#5) / DDD context modeling (#4) are demonstrated by only one small concrete feature (Notifications), with the real surface living in MMCA.Store / MMCA.ADC. The six N/A front-end categories (Accessibility, Responsive Design, Forms, Navigation, Front-End Security, i18n) were excluded because the framework ships infrastructure rather than end-user screens.
