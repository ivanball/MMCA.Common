# Architecture Evaluation Criteria

A structured rubric for evaluating the architecture of an enterprise application. Each
category defines **what** is being assessed, **concrete criteria** to check, **red flags**
that signal trouble, and a **maturity scale** so scores are comparable across reviews.

Although written against a .NET / DDD / Clean Architecture / CQRS stack (modular monolith
evolving toward microservices) with a Blazor / MudBlazor front end, the criteria generalize
to any enterprise codebase and component-based UI.

The categories are organized in three parts: **Part A — Application / Backend Architecture**
(§1–17), **Part B — Front-End / UI Architecture** (§18–28), and **Part C — Operational,
Governance & Cross-Cutting Concerns** (§29–34).

---

## How to Use This Rubric

### Maturity scale (per category)

Score each category 0–4. Use the same scale everywhere so totals are comparable.

| Level | Name | Meaning |
|-------|------|---------|
| 0 | **Absent** | Principle/pattern not applied; actively violated. |
| 1 | **Initial** | Ad-hoc, inconsistent, present only in isolated spots. |
| 2 | **Developing** | Applied in most new code; legacy gaps remain; some confusion. |
| 3 | **Consistent** | Applied uniformly with conventions; enforced by review. |
| 4 | **Optimized** | Enforced automatically (analyzers/tests/CI), documented, and evolved deliberately. |

### Implementation score (per category)

Alongside the maturity level, rate **how well each category is actually implemented** on a finer
0–10 scale. The two axes measure different things and should both be recorded:

- **Maturity (0–4)** — *process*: how consistently and how well-governed the pattern is (ad-hoc → enforced by CI).
- **Implementation (0–10)** — *substance*: how good the implementation is right now, judged against the category's **criteria** and **red flags**.

A category can be mature-but-mediocre (enforced conventions wrapped around a weak design) or
excellent-but-inconsistent (a strong implementation applied only in spots) — two scores capture that
difference where one cannot.

| Score | Band | Meaning |
|-------|------|---------|
| 0 | **None** | Category not implemented; criteria unmet, red flags pervasive. |
| 1–2 | **Poor** | Isolated, partial attempts; major gaps; multiple red flags present. |
| 3–4 | **Partial** | Several criteria met; meaningful gaps remain; some red flags. |
| 5–6 | **Adequate** | Most criteria met; functional with rough edges; a few red flags. |
| 7–8 | **Strong** | Nearly all criteria met well; only minor, isolated gaps; no significant red flags. |
| 9–10 | **Exemplary** | All criteria met to a high standard; reference-quality. **10 = perfectly implemented — nothing left to improve.** |

**Rule of thumb:** the implementation score usually tracks maturity (≈ maturity × 2.5 as a starting
point), then nudge it up or down for execution quality the coarse maturity level can't express. A
large gap between the two axes is itself a finding worth a note.

### Scoring model

- **Weight** each category by risk to *this* system (weights below are defaults — adjust per engagement).
- **Maturity index** = Σ(category maturity score × weight) ÷ Σ(weight × 4) → a 0–100% architecture health index.
- **Implementation index** = Σ(category implementation score × weight) ÷ Σ(weight × 10) → a parallel 0–100% measure of execution quality. Compare the two indices: a lower implementation index means quality is the weaker axis; a lower maturity index means consistency/governance is.
- Capture **evidence** (file paths, PRs, ADRs) for every score. A score without evidence is an opinion.
- Re-run quarterly or per-release to trend both indices over time.

### Scorecard template

```
| # | Category                      | Weight | Maturity (0-4) | Impl (0-10) | Weighted | Evidence / Notes |
|---|-------------------------------|--------|----------------|-------------|----------|------------------|
| 1 | SOLID Principles              |   3    |                |             |          |                  |
| 2 | Design Patterns               |   2    |                |             |          |                  |
| 3 | Clean Architecture            |   3    |                |             |          |                  |
| 4 | Domain-Driven Design          |   3    |                |             |          |                  |
| 5 | Vertical Slice Architecture   |   2    |                |             |          |                  |
| 6 | CQRS & Event-Driven           |   2    |                |             |          |                  |
| 7 | Microservices Readiness       |   3    |                |             |          |                  |
| 8 | Data Architecture             |   3    |                |             |          |                  |
| 9 | API & Contract Design         |   2    |                |             |          |                  |
|10 | Cross-Cutting Concerns        |   2    |                |             |          |                  |
|11 | Security                      |   3    |                |             |          |                  |
|12 | Performance & Scalability     |   2    |                |             |          |                  |
|13 | Observability & Operability   |   2    |                |             |          |                  |
|14 | Testability & Test Strategy   |   3    |                |             |          |                  |
|15 | Best Practices & Code Quality |   2    |                |             |          |                  |
|16 | Maintainability & Evolvability|   2    |                |             |          |                  |
|17 | DevOps & Deployment           |   2    |                |             |          |                  |
|   | **— Part B: Front-End / UI —**|        |                |             |          |                  |
|18 | UI Architecture & Components  |   3    |                |             |          |                  |
|19 | State Management & Data Flow  |   3    |                |             |          |                  |
|20 | Design System & UI Consistency|   2    |                |             |          |                  |
|21 | Accessibility (a11y)          |   3    |                |             |          |                  |
|22 | Responsive & Cross-Browser    |   2    |                |             |          |                  |
|23 | Front-End Performance         |   2    |                |             |          |                  |
|24 | Forms, Validation & UX Safety |   2    |                |             |          |                  |
|25 | Navigation & Information Arch  |   2    |                |             |          |                  |
|26 | Front-End Security            |   3    |                |             |          |                  |
|27 | Internationalization (i18n)   |   1    |                |             |          |                  |
|28 | Front-End Testing & Quality   |   3    |                |             |          |                  |
|   | **— Part C: Operational & Governance —** | |          |             |          |                  |
|29 | Resilience & Business Continuity| 3    |                |             |          |                  |
|30 | Compliance, Privacy & Governance| 2    |                |             |          |                  |
|31 | Cost Efficiency / FinOps      |   2    |                |             |          |                  |
|32 | Dependency & Supply-Chain     |  2/3   |                |             |          |                  |
|33 | Developer Experience & Inner Loop| 2   |                |             |          |                  |
|34 | Architecture Governance & Docs|   2    |                |             |          |                  |
```

---

## 1. SOLID Principles

**Intent:** Object/module-level design discipline that keeps code flexible and decoupled.

**Criteria**
- **SRP** — each class/handler has one reason to change; no "god" services orchestrating unrelated concerns.
- **OCP** — behavior extended via new types/strategies/decorators, not by editing switch statements.
- **LSP** — derived types are substitutable; no `NotSupportedException` overrides or type-sniffing (`is`/`as` dispatch).
- **ISP** — interfaces are narrow and role-specific; clients aren't forced to depend on members they ignore.
- **DIP** — high-level modules depend on abstractions; concretions injected via DI, not `new`-ed inline.

**Red flags**
- Constructors with 8+ dependencies (SRP/ISP smell).
- `switch`/`if` on a type enum that grows with every feature (OCP miss).
- Domain or application code referencing concrete infrastructure classes (DIP miss).
- Interfaces with a single implementation created only to "mock everything."

**Default weight:** 3

---

## 2. Design Patterns

**Intent:** Appropriate, idiomatic use of patterns — solving real problems, not pattern theater.

**Criteria**
- Creational (Factory methods on entities, Builder, Options) used where construction is non-trivial.
- Structural (Adapter, Decorator, Facade) used for boundaries and pipelines — e.g., handler decorator pipeline for validation/logging/transactions.
- Behavioral (Strategy, Mediator/dispatcher, Specification, Observer/domain events) used for variation and decoupling.
- Domain patterns: **Result** for error flow, **Repository/Unit of Work**, **Specification** for query intent, **Outbox** for reliable messaging.
- Patterns are *named consistently* and discoverable; team shares vocabulary.

**Red flags**
- Patterns applied where a plain method would do (Singleton for stateless helpers, abstract factories with one product).
- Anemic "manager/helper/util" classes that hide procedural code.
- Reinvented infrastructure (hand-rolled mediator/retry) where a vetted library exists.
- Exceptions used for control flow where a Result/validation result is the established convention.

**Default weight:** 2

---

## 3. Clean Architecture

**Intent:** Dependencies point inward; business rules are independent of frameworks, UI, and data stores.

**Criteria**
- **Dependency rule** enforced: Domain → (nothing); Application → Domain; Infrastructure/API/UI → inward only. Verified by project references and ideally an architecture test (NetArchTest/ArchUnitNET).
- **Domain purity** — no EF, ASP.NET, serialization, or framework attributes in the domain layer.
- **Ports & adapters** — application defines interfaces (ports); infrastructure implements them (adapters).
- **Use-case centric** — application layer expresses business operations, not CRUD-on-tables.
- Framework concerns (DI wiring, middleware, persistence) live at the outermost ring.

**Red flags**
- Domain entities decorated with `[Table]`, `[JsonProperty]`, or referencing `DbContext`.
- Controllers/endpoints calling repositories directly, bypassing the application layer.
- "Shared kernel" turning into a dumping ground that couples everything.
- Circular references between layers or modules.

**Default weight:** 3

---

## 4. Domain-Driven Design

**Intent:** The model reflects the business; boundaries follow capability seams, not technical layers.

**Criteria**
- **Ubiquitous language** — type/method names match business terms used by stakeholders.
- **Bounded contexts / modules** with explicit boundaries and ownership (e.g., Catalog, Sales, Identity).
- **Aggregates** with clear roots and invariants enforced inside the boundary; references between aggregates by ID, not object graph.
- **Value objects** for concepts with no identity (Money, Address, EmailAddress); immutability respected.
- **Domain events** raised by aggregates to signal meaningful state changes.
- **Factory methods returning `Result<T>`** so invalid entities can't be constructed.
- Rich behavior on entities, not setters-only.

**Red flags**
- Anemic domain model — all logic in services, entities are property bags.
- Aggregates that load half the database; transactions spanning many aggregates.
- Context boundaries that leak (one module querying another's tables directly).
- Primitive obsession (raw `string`/`Guid`/`decimal` instead of strong types and identifier aliases).

**Default weight:** 3

---

## 5. Vertical Slice Architecture

**Intent:** Code is organized by feature/capability, so a change touches one cohesive slice.

**Criteria**
- Features grouped by use case (command/query + handler + validator + DTO together), not by horizontal technical folders.
- Each slice is independently understandable and testable; minimal shared mutable state.
- Cross-cutting concerns handled by the pipeline (decorators/behaviors), not duplicated per slice.
- Adding a feature means adding a slice, rarely editing shared switchboards.
- Coupling *within* a slice is high (cohesive); coupling *between* slices is low.

**Red flags**
- "Layered-by-type" sprawl where one feature spreads across `Services/`, `Repositories/`, `DTOs/`, `Validators/` with no locality.
- Shared base handlers/services that every slice must modify.
- Slices reaching into each other's internals instead of going through a defined contract or event.

**Default weight:** 2

---

## 6. CQRS & Event-Driven Design

**Intent:** Reads and writes are separated where it pays off; integration via events is reliable.

**Criteria**
- Commands (mutate, return Result) and queries (read, side-effect-free) are distinct.
- Handler dispatch via a mediator/dispatcher with a decorator pipeline (validation → logging → transaction → handler).
- Read models/projections shaped for the consumer where read/write asymmetry warrants it.
- **Outbox pattern** for atomic persist-then-publish; no "save then publish and hope."
- Idempotent consumers; events carry enough context; versioning strategy for event schemas.
- Eventual consistency boundaries are explicit and documented (ADR).

**Red flags**
- "CQRS" that's just two folders sharing one fat model with no real benefit.
- Dual-write to DB and broker without an outbox (lost-message risk).
- Synchronous chains of commands masquerading as events.
- Consumers that aren't idempotent and break on redelivery.

**Default weight:** 2

---

## 7. Microservices Readiness

**Intent:** Whether services (or future-extractable modules) are independently deployable and own their data.

**Criteria**
- **Service boundaries** align with bounded contexts; one team can own and deploy a service.
- **Data ownership** — each service/module owns its schema; **no shared writable database** across service boundaries.
- **Communication** — async events for integration, sync calls only where strong consistency is required; contracts are explicit and versioned.
- **Resilience** — timeouts, retries with backoff, circuit breakers, bulkheads, graceful degradation.
- **Independent deployability** — services build/test/deploy independently; backward-compatible contracts.
- **Distributed observability** — correlation/trace IDs propagate across service hops.
- For a **modular monolith**: modules implement a common contract, are discovered/registered in dependency order, and are extractable without rewrites.

**Red flags**
- "Distributed monolith" — services that must deploy together; chatty synchronous call graphs.
- Shared database tables read/written by multiple services.
- No resilience policies; a downstream outage cascades.
- Integration via direct DB access instead of contracts/events.

**Default weight:** 3

---

## 8. Data Architecture

**Intent:** Persistence, consistency, and migrations are deliberate and safe.

**Criteria**
- Transaction boundaries match aggregate boundaries; unit-of-work scope is clear.
- **Migrations** are versioned, reviewed, reversible/forward-only by policy, and run in CI/CD.
- **Soft-delete + global query filters** (or an intentional alternative) applied consistently.
- **Audit fields** (created/modified by/on) stamped centrally, not per-handler.
- Query efficiency: explicit eager-loading strategy; no N+1; projections for read paths.
- Concurrency handled (optimistic concurrency tokens) where contention exists.
- Per-service data isolation where microservices are in play (see §7).

**Red flags**
- Migrations hand-applied to production; schema drift between environments.
- N+1 queries, `Include` chains loading whole graphs, client-side evaluation.
- Cross-module/service joins coupling independent contexts.
- Hard deletes where soft-delete is the convention (orphaned references, lost audit trail).

**Default weight:** 3

---

## 9. API & Contract Design

**Intent:** External and inter-service contracts are clear, stable, and evolvable.

**Criteria**
- Consistent resource/endpoint design (REST/minimal APIs/gRPC) with predictable shapes.
- **Versioning** strategy for breaking changes; backward compatibility honored.
- Standardized error responses (e.g., Problem Details) and consistent status codes.
- Request validation at the edge; DTOs decoupled from domain entities (manual mapping or mapper by ADR).
- Pagination, filtering, sorting conventions are uniform.
- Contracts documented (OpenAPI) and generated/verified, not hand-maintained drift.

**Red flags**
- Domain entities serialized directly to the wire (leaks internals, couples API to schema).
- Breaking changes shipped without versioning; consumers break silently.
- Inconsistent error shapes; 200-with-error-body anti-pattern.
- Undocumented or stale API specs.

**Default weight:** 2

---

## 10. Cross-Cutting Concerns

**Intent:** Validation, caching, resilience, configuration, and mapping are centralized and consistent.

**Criteria**
- Validation, logging, transactions handled by pipeline behaviors/decorators — not copy-pasted.
- Configuration via strongly-typed options, validated at startup; environment-specific overrides clean.
- Caching strategy explicit (what, where, invalidation) and not hiding correctness bugs.
- Resilience policies (retry/timeout/circuit-breaker) applied consistently via a shared mechanism.
- Mapping strategy consistent and decided by ADR (manual vs. library).

**Red flags**
- Validation/logging duplicated in every handler.
- Magic strings for config; secrets in config files.
- Cache with no invalidation story (stale data) or caching used to mask slow queries.
- Inconsistent, per-call retry logic.

**Default weight:** 2

---

## 11. Security

**Intent:** AuthN/AuthZ, secrets, and data protection are correct by construction.

**Criteria**
- **Authentication** centralized; tokens validated; identity flows documented (e.g., dual-fetch ADR).
- **Authorization** enforced at the right layer (policy/resource-based), not just UI hiding.
- **Secrets** in a vault/managed identity, never in source or plain config; rotation possible.
- Input validation and output encoding guard against injection/XSS; parameterized queries only.
- Transport security (TLS), data-at-rest protection, PII handling, and least-privilege access.
- Dependency and package vulnerability scanning in CI (audit sources configured).
- OWASP Top 10 reviewed; rate limiting / anti-automation where exposed.

**Red flags**
- Authorization checks only in the UI; APIs callable unguarded.
- Connection strings/keys committed or in appsettings.
- Over-broad permissions (admin everywhere), no least privilege.
- Ignored package audit warnings.

**Default weight:** 3

---

## 12. Performance & Scalability

**Intent:** The system meets latency/throughput goals and scales horizontally.

**Criteria**
- Async I/O throughout; no sync-over-async; no blocking the request thread.
- Hot-path query efficiency (projections, indexes, no N+1); measured, not assumed.
- Caching at appropriate tiers with sound invalidation.
- Stateless services enabling horizontal scale; session/state externalized.
- Load/stress tested against realistic volumes; capacity provisioning evidence-based (right-sized, not guessed).
- Pagination/streaming for large result sets; backpressure on queues.

**Red flags**
- `.Result`/`.Wait()` deadlock risks; synchronous DB calls in async pipelines.
- Unbounded queries returning whole tables.
- In-memory state preventing scale-out.
- Provisioning by guesswork (massive over- or under-provisioning) with no load data.

**Default weight:** 2

---

## 13. Observability & Operability

**Intent:** You can understand and operate the system in production.

**Criteria**
- **Structured logging** with correlation/trace IDs flowing across module/service boundaries.
- **Distributed tracing** (OpenTelemetry) and **metrics** (RED/USE) wired to a backend (e.g., App Insights).
- **Health checks** (liveness/readiness) and dependency checks exposed for orchestrators.
- Alerting on SLO breaches; dashboards exist and are used.
- Noise control — high-volume/low-value telemetry (e.g., poll spans) deliberately filtered to manage cost.
- Runbooks for common failures; graceful shutdown and startup ordering.

**Red flags**
- `Console.WriteLine`/unstructured logs; no correlation across services.
- No health endpoints; orchestrator can't tell if the app is alive.
- Telemetry cost unmanaged (everything logged at Information) or, conversely, nothing logged.
- No alerts — failures discovered by users.

**Default weight:** 2

---

## 14. Testability & Test Strategy

**Intent:** The design supports fast, reliable, meaningful tests at the right levels.

**Criteria**
- Healthy **test pyramid**: many fast unit tests on domain/application, fewer integration, few E2E.
- Domain logic testable without infrastructure (pure, injectable dependencies).
- Integration tests cover persistence, messaging, and module wiring against real-ish dependencies (Testcontainers/Aspire).
- Shared test infrastructure (page objects, fixtures, base classes) reused across consumers, not duplicated.
- **Architecture tests** enforce dependency rules automatically.
- Tests are deterministic, isolated, and run in CI as a gate.
- Coverage tracked on meaningful paths (not chased as a vanity number).

**Red flags**
- Inverted pyramid (mostly slow E2E), flaky tests, tests disabled/skipped without tracking.
- Logic only reachable through HTTP, forcing heavy integration tests for unit-level concerns.
- Mock-everything tests that assert implementation details, not behavior.

**Default weight:** 3

---

## 15. Best Practices & Code Quality

**Intent:** Day-to-day craftsmanship that keeps the codebase healthy.

**Criteria**
- **Analyzers at error severity** (style, security, threading, maintainability) enforced in CI; warnings-as-errors.
- Consistent conventions (file-scoped namespaces, naming, immutability, nullability) applied uniformly.
- **Central package management**; pinned, audited dependencies; intentional version policy (e.g., a library pinned for licensing reasons).
- Error handling via the established pattern (Result vs. exceptions) used consistently.
- Code is self-documenting; comments explain *why*, not *what*; ADRs capture significant decisions.
- Dead code, TODOs, and suppressions are tracked, not accumulated.

**Red flags**
- Disabled analyzers / blanket `#pragma warning disable`.
- Mixed conventions, inconsistent error handling.
- Unpinned or unaudited dependencies; accidental major-version bumps reintroducing known-bad packages.

**Default weight:** 2

---

## 16. Maintainability & Evolvability

**Intent:** The system absorbs change cheaply and ages well. (The *governance/documentation depth* behind this — ADRs, fitness functions, diagrams — is scored separately in §34.)

**Criteria**
- Low coupling / high cohesion measured (dependency graphs, change-coupling); modules swap independently.
- Clear module/package boundaries with explicit, versioned contracts (e.g., shared framework packages consumed downstream).
- **Consistent upgrade strategy** — framework changes rolled out to all consumers together (no long-lived divergent versions) per team policy.
- Documentation that stays current: ADRs for the *why*, architecture map for the *what*.
- Onboarding cost is low; conventions discoverable.
- Tech-debt register exists and is serviced.

**Red flags**
- Shotgun surgery — one change touches many modules.
- Divergent versions of a shared library across consumers; partial rollouts that linger.
- Tribal knowledge; undocumented decisions; stale docs contradicting the code.

**Default weight:** 2

---

## 17. DevOps & Deployment

**Intent:** Building, releasing, and provisioning are automated, repeatable, and safe. (The *local developer experience / inner loop* behind this — local orchestration, cross-repo dev, build speed — is scored separately in §33.)

**Criteria**
- **CI** gates: build, analyzers, tests, security/audit on every PR; fast feedback.
- **CD** with repeatable, automated deployments; rollback strategy.
- **Infrastructure as Code** (Bicep/Terraform) — environments reproducible; no click-ops drift.
- Secrets/identity via managed identity / OIDC, least privilege for deployment principals.
- Environment parity; configuration externalized per environment.
- Cost awareness — provisioning right-sized with evidence; temporary scale-ups tracked with revert plans.
- Containerization/orchestration (or Aspire-style local-to-cloud parity) where applicable.

**Red flags**
- Manual deploys; environment drift; "works on the build server."
- Infra changed by hand in the portal; no IaC source of truth.
- Long-lived elevated credentials; secrets in pipelines.
- Scale-ups left running after the event that needed them.

**Default weight:** 2

---

# Part B — Front-End / UI Architecture

Categories §18–28 assess the presentation tier. They use the same 0–4 maturity scale and
weighting model as Part A. Where a concern has a backend counterpart (security, performance,
testing), Part B focuses on the **client/UI-specific** facets and cross-references Part A.

---

## 18. UI Architecture & Component Design

**Intent:** Components are cohesive, reusable, and composed cleanly — the UI has a deliberate structure, not page-sized blobs.

**Criteria**
- **Container/presentational split** — smart components own data/behavior; dumb components render from parameters and raise events. Logic isn't buried in markup.
- **Component contracts** — typed parameters with sensible defaults; outputs via callbacks/`EventCallback`; two-way binding used intentionally, not everywhere.
- **Composition over inheritance** — layouts, render fragments, and slots compose UI; minimal deep component hierarchies.
- **Reuse** — shared/primitive components live in a common UI library (consumed by multiple apps), not copy-pasted per page.
- **Render lifecycle discipline** — expensive work kept out of render; `ShouldRender`/keys/`@key` used to control re-render where it matters.
- **Separation of concerns** — no direct data-access or business rules in components; they call application services/clients.

**Red flags**
- 1000-line page components mixing data fetching, validation, and markup.
- Business logic in `.razor` code-behind that belongs in the application layer.
- Prop-drilling many levels deep instead of composition or scoped state.
- Components that reach into global singletons for data instead of receiving it.

**Default weight:** 3

---

## 19. State Management & Data Flow

**Intent:** Client state has a clear owner and predictable flow; server state is cached and invalidated deliberately.

**Criteria**
- **Single source of truth** per piece of state; ownership is explicit (component-local vs. scoped service vs. global store).
- **Unidirectional data flow** — state flows down via parameters, changes flow up via events; avoid hidden mutation of shared objects.
- **Server vs. client state distinguished** — fetched data cached with a staleness/invalidation strategy; not refetched on every render.
- **Component communication** via well-defined channels (cascading values, scoped state services, mediators) — not static mutable globals.
- **Render correctness** — `StateHasChanged` called intentionally; async state updates marshalled to the UI thread/context correctly.
- **Lifetime correctness** — scoped vs. singleton services chosen correctly for the hosting model (Server vs. WASM); no accidental cross-user state leakage.

**Red flags**
- Static mutable fields holding user/session state (leaks across users in Blazor Server).
- Stale UI because a parent mutated state but didn't notify children (e.g., wrapped guard components reading a stale `IsDirty` because the parent didn't `StateHasChanged()` before navigating).
- Refetching/recomputing on every render; no memoization or caching.
- "Spooky action at a distance" — multiple components mutating one shared object.

**Default weight:** 3

---

## 20. Design System, Theming & UI Consistency

**Intent:** A coherent visual language enforced by a component library, not re-implemented per screen.

**Criteria**
- **Component library used consistently** (e.g., MudBlazor) — teams build on it rather than bypassing it with raw HTML/CSS.
- **Design tokens / theme** centralized (palette, typography, spacing, breakpoints); dark/light or brand variants driven from the theme.
- **Consistency** — spacing, density, iconography, button hierarchy, empty/loading states look the same across pages.
- **Encapsulated overrides** — custom styling wrapped in reusable components, not scattered inline styles or `!important` overrides.
- **Known-issue guardrails** — wrappers/conventions exist around library quirks so every page doesn't re-hit the same bug (e.g., a grid wrapper that normalizes paging/sorting behavior).

**Red flags**
- Mixed component libraries or hand-rolled controls duplicating library ones.
- Inline styles and magic pixel values everywhere; no shared tokens.
- Inconsistent loading/empty/error treatments per page.
- Library defaults fought page-by-page instead of fixed once in a shared wrapper.

**Default weight:** 2

---

## 21. Accessibility (a11y)

**Intent:** The UI is usable by everyone, including assistive-technology users — and ideally enforced, not aspirational.

**Criteria**
- **Semantic structure** — correct landmarks/headings/lists; interactive elements are real buttons/links, not click-handlers on `div`s.
- **Keyboard operability** — everything reachable and operable by keyboard; logical tab order; visible focus; no keyboard traps.
- **ARIA where needed** — names/roles/states on custom widgets; live regions for async updates; relies on native semantics first.
- **Color & contrast** — meets WCAG 2.1 AA contrast; information not conveyed by color alone.
- **Forms** — labels associated with inputs; errors announced and programmatically linked to fields.
- **Verification** — automated checks (axe/Lighthouse) in CI plus periodic manual screen-reader/keyboard passes; target conformance level stated (e.g., WCAG 2.1 AA).

**Red flags**
- `div`/`span` click handlers with no role/keyboard support.
- Placeholder-as-label; unlabeled icon buttons; missing alt text.
- Focus lost or invisible after navigation/dialog open.
- No a11y testing in CI; accessibility treated as a post-launch fix.

**Default weight:** 3

---

## 22. Responsive Design & Cross-Browser/Device

**Intent:** The UI works across viewport sizes, input modes, and supported browsers.

**Criteria**
- **Fluid/responsive layouts** via the design system's grid/breakpoints; no fixed-width desktop-only screens.
- **Touch and pointer** both supported; adequate target sizes; no hover-only affordances for critical actions.
- **Supported matrix defined** (browsers/devices) and verified; graceful degradation outside it.
- **Content reflow** — tables/grids/dialogs adapt or provide mobile alternatives; no horizontal scrolling of core content.
- **Density options** where data-dense (comfortable/compact) without breaking layout.

**Red flags**
- Pixel-perfect desktop layouts that break below a breakpoint.
- Data grids unusable on mobile with no alternative view.
- Hover-only menus/tooltips hiding essential actions on touch.
- "Works in Chrome" with no other-browser verification.

**Default weight:** 2

---

## 23. Front-End Performance & Rendering

**Intent:** The UI loads and responds fast; rendering work is bounded. (Complements §12 — this is the client side.)

**Criteria**
- **Initial load** — bundle/payload size controlled; lazy-loading/code-splitting for heavy routes; prerender/SSR where it helps perceived speed.
- **Render efficiency** — avoid unnecessary re-renders (`ShouldRender`, `@key`, stable callbacks); virtualization for long lists/grids.
- **Data efficiency** — server-side paging/filtering/sorting for large sets (not loading everything client-side); debounced inputs.
- **Perceived performance** — skeletons/optimistic UI/loading states; interactions stay responsive (no blocking the UI thread/circuit).
- **Asset hygiene** — images sized/compressed; fonts/icons subset; caching headers set.
- **Measured** — Core Web Vitals (LCP/INP/CLS) or equivalent tracked, not assumed.

**Red flags**
- Loading entire datasets into the client then paging in memory.
- Re-rendering large trees on every keystroke; janky typing.
- No virtualization on long grids/lists.
- Blocking the Blazor Server circuit / UI thread with sync work.

**Default weight:** 2

---

## 24. Forms, Validation & UX Safety

**Intent:** Data entry is safe, forgiving, and consistent — users don't lose work or get confused by errors.

**Criteria**
- **Validation parity** — client-side validation for fast feedback mirrors server-side rules (server remains authoritative; see §11).
- **Error presentation** — field-level, human-readable, tied to inputs; summary for form-level errors; consistent styling.
- **Dirty tracking & unsaved-changes guards** — navigating away from an edited form prompts; guard reads *current* dirty state reliably.
- **States covered** — loading, submitting (disabled/!double-submit), success, empty, and error states all designed.
- **Forgiving input** — sensible defaults, input masks/formatters, undo where feasible; destructive actions confirmed.
- **Accessibility of validation** — errors announced and associated with fields (ties to §21).

**Red flags**
- Client validation that disagrees with the server (false pass/fail).
- Silent data loss on navigation; or a guard that misfires because it reads stale dirty state.
- Double-submit allowed; no disabled/spinner state on submit.
- Generic "an error occurred" with no field context.

**Default weight:** 2

---

## 25. Navigation, Routing & Information Architecture

**Intent:** Users can find their way; routes are meaningful, guarded, and role-aware.

**Criteria**
- **Route design** — clean, bookmarkable, deep-linkable URLs; parameters typed and validated.
- **Guards & authorization** — route-level auth enforced (UI hiding is not security; see §11); unauthorized access redirects sensibly.
- **Role-based flows** — navigation reflects actor roles; flows documented (e.g., per-actor navigation diagrams) and match the implementation.
- **Wayfinding** — breadcrumbs/active states/back behavior consistent; not-found and forbidden pages handled.
- **State on navigation** — query/route state preserved appropriately; in-progress work protected (ties to §24).

**Red flags**
- Menu items visible to roles that can't use them (or hidden items still routable).
- Non-bookmarkable state hidden entirely in memory.
- Navigation flows that drift from the documented per-role design.
- No handling for 404/403 within the app shell.

**Default weight:** 2

---

## 26. Front-End Security

**Intent:** The client doesn't become the weak link — XSS, token handling, and trust boundaries are correct. (Complements §11.)

**Criteria**
- **Output encoding / XSS** — no unsanitized HTML injection (`MarkupString`/`innerHTML` only on trusted, sanitized content); user content encoded by default.
- **Token & session handling** — auth tokens stored and transmitted safely; minimal sensitive data in the browser; logout clears state.
- **Content Security Policy** and security headers configured; third-party scripts vetted.
- **No secrets in the client** — API keys/secrets never shipped to WASM/browser bundles.
- **Client is untrusted** — all authorization re-checked server-side; client checks are UX only.
- **Dependency hygiene** — JS/interop and front-end packages audited for vulnerabilities.

**Red flags**
- Rendering user input as raw HTML/markup without sanitization.
- Secrets or privileged config embedded in client bundles.
- Auth tokens in insecure storage or logged.
- Authorization enforced only in the UI.

**Default weight:** 3

---

## 27. Internationalization & Localization

**Intent:** The UI can be translated and respects culture — if in scope. (Score weight 0–1 if single-locale by design.)

**Criteria**
- **Externalized strings** — UI text in resource files, not hard-coded; keys consistent.
- **Culture-aware formatting** — dates, numbers, currency, time zones formatted per culture, not hard-coded.
- **Layout tolerance** — components handle text expansion and, where required, RTL.
- **Locale selection** — discoverable, persisted; server and client cultures aligned.
- **Pluralization & interpolation** handled by the i18n mechanism, not string concatenation.

**Red flags**
- Hard-coded user-facing strings throughout markup.
- Manual date/number formatting ignoring culture.
- Layouts that break with longer translations.
- Concatenated sentences impossible to translate correctly.

**Default weight:** 1

---

## 28. Front-End Testing & Quality

**Intent:** The UI is verified at the right levels with stable, meaningful tests. (Complements §14.)

**Criteria**
- **Component tests** (e.g., bUnit) for rendering logic, parameters, events, and conditional UI.
- **End-to-end tests** (e.g., Playwright) for critical user journeys; shared E2E infrastructure (page objects, fixtures, abstract bases) reused across apps rather than duplicated.
- **Accessibility checks** (axe/Lighthouse) and ideally **visual-regression** tests run in CI.
- **Stability** — tests use robust selectors (roles/test-ids), avoid timing flakiness, run as a merge gate.
- **Coverage of states** — loading/empty/error/edge states tested, not just the happy path.
- **Right pyramid** — many fast component tests, fewer broad E2E flows.

**Red flags**
- Only manual click-testing; no automated UI tests.
- Flaky, sleep-based E2E using brittle CSS-path selectors.
- Duplicated E2E setup per app instead of a shared package.
- Happy-path-only coverage; a11y untested.

**Default weight:** 3

---

# Part C — Operational, Governance & Cross-Cutting Concerns

Categories §29–32 span backend and front end and are judged over the system's full lifecycle.
They use the same 0–4 maturity scale and weighting.

---

## 29. Resilience, Reliability & Business Continuity

**Intent:** The system survives partial failure and recovers from disaster within defined objectives. (Extends the resilience facets of §7/§12 into a first-class recovery story.)

**Criteria**
- **Failure isolation** — timeouts, retries with backoff + jitter, circuit breakers, bulkheads; one failing dependency doesn't cascade.
- **Graceful degradation** — fallbacks/queued work when a dependency is down; the broker buffers integration events and the outbox guarantees eventual delivery after recovery.
- **Backup & restore** — automated backups for every stateful store (per-service DBs); **restores actually tested**; documented procedure.
- **Disaster recovery** — defined **RTO/RPO** per service; failover/multi-region strategy, or a conscious and documented acceptance of single-region risk.
- **Reliability targets** — SLOs/error budgets defined and measured; health/readiness probes drive orchestration and auto-heal.
- **Failure testing** — chaos/fault injection or at least documented failure-mode analysis; startup ordering and graceful shutdown verified.

**Red flags**
- Backups that have never been restored (untested recovery).
- Undefined RTO/RPO; "we'd figure it out."
- Retries without backoff/idempotency (retry storms, duplicate side effects).
- Single points of failure with no failover and no explicit, documented risk acceptance.

**Default weight:** 3

---

## 30. Compliance, Privacy & Data Governance

**Intent:** Personal and regulated data is classified, governed, and handled lawfully across its lifecycle. (§11 defends against attackers; this answers to regulators.)

**Criteria**
- **PII/sensitive-data inventory** — what's collected, where it's stored, who can access it.
- **Retention & purge** — defined retention periods with an actual purge mechanism; **reconcile soft-delete with right-to-erasure** — soft-delete preserves rows, erasure requires real removal/anonymization, so a hard-delete/anonymize path must exist for subject requests.
- **Data-subject rights** — export/access and deletion requests supported operationally, not just in theory.
- **Data residency & sovereignty** — storage region matches regulatory/contractual requirements.
- **Consent & lawful basis** — captured where required and auditable.
- **Audit trail** — audit fields + access logs support accountability and legal hold.

**Red flags**
- Soft-delete as the *only* deletion path, with no erasure mechanism (direct GDPR/CCPA conflict).
- No retention policy — data kept forever by default.
- PII in logs/telemetry; PII replicated into read models/caches without governance.
- Residency requirements unverified for the chosen region.

**Default weight:** 2

---

## 31. Cost Efficiency / FinOps

**Intent:** Cloud spend is proportional to value and driven by data, not guesswork. (§17 mentions cost; this makes it a first-class axis.)

**Criteria**
- **Right-sizing** — compute/database tiers matched to *measured* load; scaling rules backed by real traffic, not worst-case guesses.
- **Reversible scale events** — temporary scale-ups for known peaks have an automated or scheduled **revert**; nothing stays scaled up after the event.
- **Telemetry/log cost control** — retention tuned, high-volume/low-value signals filtered, sampling where appropriate (logs and traces are a real line item).
- **Resource lifecycle** — dev/test/ephemeral resources deprovisioned; orphaned resources reaped.
- **Cost visibility** — spend attributable per service/environment; budgets/alerts; cost considered in design (poll intervals, chatty calls).
- **Tier fit** — shared/serverless/Basic tiers used for intermittent or archival workloads.

**Red flags**
- Provisioning by guesswork — large over- (or under-) provisioning with no load evidence.
- Scale-ups left running after the event that justified them.
- Unbounded log/trace retention; everything emitted at Information.
- No per-service/environment cost attribution; surprises on the bill.

**Default weight:** 2

---

## 32. Dependency & Supply-Chain Management

**Intent:** Third-party and inter-package dependencies are controlled, auditable, and evolve safely — especially critical for a framework that publishes packages. (Elevates §15's hygiene into release + provenance.)

**Criteria**
- **Pinned, central versions** (central package management); deliberate, reviewed upgrades — no accidental major bumps reintroducing known-bad versions (a library pinned for licensing must stay pinned).
- **Vulnerability auditing in CI** across all feeds — including private/GitHub Packages, with audit-source config so private feeds don't break the build.
- **Published-package versioning** — semantic versioning + a breaking-change policy for the framework's packages, so consumers know what an upgrade implies.
- **Coordinated rollout** — framework changes swept across all consumers together; no long-lived divergent versions or lingering partial rollouts.
- **Provenance & integrity** — SBOM, lock files, trusted sources only; transitive dependencies reviewed.
- **License compliance** — dependency licenses tracked; commercial-license constraints honored.

**Red flags**
- Blanket "update all packages" that reintroduces a pinned/known-bad dependency.
- Private-feed audit warnings ignored or suppressed wholesale.
- Consumers stranded on divergent framework versions.
- No SBOM/lock files; unaudited transitive dependencies; unreviewed licenses.

**Default weight:** 2 (raise to **3** for a published framework such as MMCA.Common)

---

## 33. Developer Experience & Inner Loop

**Intent:** Developers build, run, test, and iterate locally with fast, low-friction feedback. (Promoted out of §17 — that scores release/ops automation; this scores the *inner* loop.)

**Criteria**
- **Fast inner loop** — build and test times kept low; incremental builds; hot reload where available; tests fast enough to run constantly.
- **One-command local run** — local orchestration brings up the whole system (e.g., Aspire AppHost spins up services, dependencies, and dashboards) with no manual wiring.
- **Cross-repo local dev** — a frictionless way to develop the shared framework alongside consumers *without publishing* (e.g., a `local.props` override pointing at `../MMCA.Common/Source`); documented and gitignored.
- **Onboarding** — a new dev is productive quickly; prerequisites, secrets bootstrap, and run instructions documented and current.
- **Consistent tooling** — analyzers/formatting/`.editorconfig` enforce the same rules locally as CI; fast pre-merge feedback.
- **Local/cloud parity** — local topology mirrors production (Aspire-to-Azure) so integration bugs surface locally, not in prod.
- **Discoverable commands** — build/test/migrate/run are each one obvious command.

**Red flags**
- Multi-step manual setup to run locally; tribal knowledge required.
- Slow builds/tests that discourage running them.
- No cross-repo dev path — must publish a package to test a framework change.
- Local environment diverges from prod (different brokers/DBs) hiding integration bugs.

**Default weight:** 2

---

## 34. Architecture Governance & Documentation

**Intent:** Decisions are recorded, conformance is enforced, and the system is documented so it stays coherent as it evolves. (Promoted out of §16 — that scores the *property* of evolvability; this scores the *machinery* that protects it.)

**Criteria**
- **Decision records** — significant decisions captured as ADRs with context/rationale/consequences, and kept current (e.g., the `ADRs/` set on manual mapping, navigation populators, outbox dual-dispatch, auth dual-fetch).
- **Fitness functions** — architecture rules enforced *automatically* as executable governance (architecture tests / NetArchTest), not just prose.
- **Living documentation** — an architecture map/overview (C4, or a dependency-ordered class encyclopedia) that matches the code and is maintained.
- **Documented conventions** — contributor guides (`CLAUDE.md`) describe patterns, layering rules, and how to extend; discoverable and current.
- **Change governance** — a lightweight process for evolving cross-cutting patterns; consistency enforced across modules and repos.
- **Traceability** — docs link to code/ADRs; stale docs are detected and pruned.

**Red flags**
- Significant decisions live only in people's heads; ADRs absent or stale.
- Architecture rules exist only as prose nobody checks (no fitness functions).
- Diagrams/docs that contradict the code (worse than none).
- No documented conventions; every module reinvents patterns.

**Default weight:** 2

---

## Appendix: Quick-Scan Checklist

A 2-minute triage before the full evaluation — any "no" warrants a deeper look.

- [ ] Can you draw the dependency graph and is it acyclic and inward-pointing?
- [ ] Is the domain layer free of framework references?
- [ ] Does each service/module own its own data?
- [ ] Is there an outbox (or equivalent) guaranteeing persist-then-publish atomicity?
- [ ] Are authorization checks enforced server-side at the application boundary?
- [ ] Are secrets out of source and config?
- [ ] Do analyzers + tests gate every merge?
- [ ] Are migrations automated and reviewed?
- [ ] Do traces/correlation IDs span service hops?
- [ ] Is provisioning sized from load data rather than guesswork?
- [ ] Are ADRs present for the non-obvious decisions?
- [ ] Can a new feature be added as one cohesive slice?

**Front-end / UI:**
- [ ] Is UI logic out of components and in the application layer (components stay presentational)?
- [ ] Does each piece of client state have one clear owner, with no static mutable cross-user state?
- [ ] Is the component library/design system used consistently instead of bypassed page-by-page?
- [ ] Are interactive elements real controls (keyboard-operable, labeled), with a stated WCAG target?
- [ ] Do large grids/lists use server-side paging and virtualization rather than loading everything client-side?
- [ ] Do edit forms guard against unsaved-changes loss with reliable dirty tracking?
- [ ] Are routes guarded server-side (UI hiding is not authorization) and role flows matching the docs?
- [ ] Is user content encoded/sanitized, with no secrets in client bundles?
- [ ] Are there automated component + E2E tests (with shared E2E infra) gating merges?

**Operational / governance:**
- [ ] Has a database restore actually been tested, with defined RTO/RPO per service?
- [ ] Do retries use backoff + idempotency, and does the broker/outbox guarantee delivery after recovery?
- [ ] Is there a real erasure/anonymize path reconciling soft-delete with right-to-be-forgotten?
- [ ] Is data residency verified for the region, and is PII kept out of logs/telemetry?
- [ ] Is provisioning sized from load data, with temporary scale-ups auto-reverted?
- [ ] Are log/trace retention and volume tuned for cost?
- [ ] Are package versions pinned/audited, with coordinated cross-consumer upgrades and licenses honored?
- [ ] Can the whole system be run locally with one command, and the framework developed against consumers without publishing?
- [ ] Are architecture rules enforced by fitness functions (tests), and are ADRs + the architecture map current and matching the code?
