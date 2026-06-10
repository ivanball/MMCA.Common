# MMCA.Common — Architecture Remediation Backlog

Derived from `ArchitectureScorecard.md` (architecture-health index **80%**, 218/272).
Tasks are every applicable category scoring **< 4**, ranked by **priority = (4 − score) × weight**.
Higher priority = bigger weighted gap = more index points per unit of effort.

**Scope:** 18 remediation items across 20 categories (two share a fix).
8 categories already score 4 (protect, don't regress); 6 are N/A for a framework.

> **Two fixes each clear multiple items — do them once:**
> - **MassTransit v8 guard** closes the medium red flags in **#32** *and* **#16**.
> - **bUnit component tests** lift **#28** *and* **#18** (and cover the #19 guard bug).

---

## Progress — first wave (2026-06-08)

Implemented in MMCA.Common — ✅ **verified 2026-06-09**: `dotnet build -c Release` is clean (0 warnings / 0 errors, all analyzers) and all 9 test projects pass (~1,611 tests, 0 failures), including 28 architecture tests (3 new MassTransit fitness cases) and 90 UI tests (6 new bUnit tests). *No `GITHUB_TOKEN` is needed — MMCA.Common restores entirely from nuget.org.*

- ✅ **#32 / #16 — MassTransit v8 fitness test.** `DependencyVersionTests` parses `Directory.Packages.props` and fails if the MassTransit major hits 9. *Remaining in #32: lock files, SBOM, CHANGELOG/versioning policy.*
- ✅ **#29 / #6 — broker retry policy.** `ConfigureBrokerTransport` applies `UseMessageRetry` (exponential) on both RabbitMQ and Azure Service Bus, configurable via new `MessageBusSettings.RetryLimit` / `RetryMinIntervalSeconds` / `RetryMaxIntervalSeconds`; `IntegrationEventConsumer` comment + log corrected. *Delayed redelivery deliberately omitted (needs the RabbitMQ delayed-exchange plugin absent from the Aspire container). Remaining in #29: RTO/RPO, restore drill, alerting. Remaining in #6: consumer inbox/dedup + event Id.*
- 🟡 **#28 / #18 — bUnit harness.** Added `bunit` (pinned **2.0.66** — v2 `BunitContext`/`Render` for xUnit v3), `BunitTestBase` (MudServices + loose JSInterop), and 6 passing tests for `EmptyState` + `MobileCardList`. *Remaining: MobileInfiniteScrollList + UnsavedChangesGuard tests, axe-core a11y, E2E-in-CI.*
- ✅ **#30 — compliance seam (partial).** `IAnonymizable` erasure seam (Domain), `OutboxCleanupService` purging processed rows older than `Outbox:RetentionDays` (default 7), and **ADR-005**. *Remaining (consumer-side): make PII entities `IAnonymizable`, add erasure/DSR + export endpoints, stop logging PII.*

## Progress — second wave (2026-06-09)

✅ **Verified**: `dotnet build -c Release` clean (0/0) and all 9 test projects pass (1,511 tests, 0 failures).

- ✅ **#32 / #16 — supply-chain.** NuGet **lock files** (`RestorePackagesWithLockFile`, 20 committed), `nuget.config` **packageSourceMapping** (`*`→nuget.org), **CycloneDX SBOM** step in `release.yml`, **CHANGELOG.md** + **VERSIONING.md** (SemVer + breaking-change + consumer-sweep policy). With the Wave-1 fitness test, #32 and #16 reach 4.
- ✅ **#11 — security.** CI **vuln-audit gate** (`dotnet list package --vulnerable` + `NuGetAudit=all`) and **SECURITY.md** (security model, OWASP note, consumer responsibilities). *Item 13 (NetArchTest security invariants) deferred to consumer suites — infeasible as NetArchTest, and the framework's CORS / anonymous-endpoints are already correct.*
- ✅ **#13 — observability.** `AddMeter("MMCA.Common.Outbox")` (dead-letter counter now exported) + **CQRS RED histograms** (`cqrs.command/query.duration`, tagged by name + outcome) via `CqrsMetrics`, registered in Aspire `WithMetrics`.
- ✅ **#17 — DevOps.** `.github/dependabot.yml` (nuget + actions, MassTransit-major ignored); symbols switched to **embedded** (orphan `snupkg` removed — verified via `dotnet pack`).
- ✅ **#34 — governance.** Refreshed the stale DB-per-service passages in `ArchitecturalAnalysis.md`; added **ADR-006** (database-per-service) + **ADR-007** (gRPC extraction) + **ADRs/README.md** index.
- ✅ **#9 — contracts (partial).** Corrected the `ServiceContractAttribute` doc (no longer claims a framework test that doesn't exist; enforcement is the consumer's). *OpenAPI generation deferred.*

## Progress — third wave (front-end, 2026-06-09)

✅ **Verified**: build clean (0/0) and all 9 test projects pass (1,519 tests, 0 failures); UI tests 90 → 98 (8 new bUnit tests).

- ✅ **#19 — UnsavedChangesGuard live-accessor.** Added optional `Func<bool>? IsDirtyAccessor`; the guard reads current dirty state at navigation time (`CurrentIsDirty`), fixing the one-render param-lag foot-gun. Additive/non-breaking; covered by bUnit tests.
- ✅ **#23 — MobileInfiniteScrollList cap.** New `MaxRenderedItems` (default 500) bounds DOM growth — infinite scroll stops fetching at the cap. (`Virtualize` would conflict with the IntersectionObserver loader.) Covered by a bUnit test.
- ✅ **#28 / #18 — bUnit coverage.** Added tests for `MobileInfiniteScrollList`, `UnsavedChangesGuard`, and the `PageError`/`PageLoading`/`PageHeader` primitives.
- ✅ **#20 — design-system (partial).** Collapsed the duplicated `#1565C0` brand hex to the `--mmca-primary` / `--mmca-primary-dark` CSS vars (single CSS source) + a sync note in `MMCATheme`. *Bootstrap→MudBlazor NavMenu chrome migration deferred (riskier).*
- ✅ **#28 / #5 — axe-core a11y.** Added `Deque.AxeCore.Playwright` (4.7.2) + a `Page.AssertNoAccessibilityViolationsAsync()` helper to the shipped E2E package (compiles here; the assertion runs in consumer E2E flows).

*Deferred (no host / larger / low value): browser-journey-in-Common-CI (Common is a library — no app to run E2E against), the Bootstrap NavMenu migration, and the EditorRequired convention check.*

## Progress — fourth wave (breaking changes + consumer sweep, 2026-06-09)

✅ **Verified across all three repos** (built/tested via `local.props` against Common *source* — no token): Common **1,523**, ADC **1,241**, Store **1,088** tests, **0 failures**; all CI solutions build 0/0.

- ✅ **#16 — `UserNotification.Create` → `Result<UserNotification>`** (Common-internal; 4 call sites updated — no consumer code calls it).
- ✅ **#4 / #15 — aggregate-factory fitness test.** `AggregateConventionTests` reflects over the Domain assembly asserting each aggregate root has a static `Create` returning `Result<T>`. Cross-aggregate-nav rule deliberately omitted (navigation-populator pattern, ADR-002). Consumers' 15 aggregates already comply.
- ✅ **#6 / #19 — consumer-side idempotency (inbox).** `MessageId` on `BaseDomainEvent`/`IDomainEvent`; `InboxMessage` entity + EF config; `IInboxStore` (`EfInboxStore`/`NoOpInboxStore`) with dedup in `IntegrationEventConsumer`; **opt-in** `MessageBus:EnableInbox` (default off); `OutboxCleanupService` also purges processed inbox rows. Unit-tested.
- ✅ **5 EF migrations** (`AddInboxMessages`): ADC Identity/Conference/Engagement/Notification (per-service DBs) + Store (shared) — each creates `InboxMessages` + the unique `MessageId` index, generated against Common source.

*Remaining (manual/opt-in): set `MessageBus:EnableInbox=true` per service once its migration is applied; optionally mirror the `Result`-return fitness assertion into ADC/Store `EntityConventionTests` (multi-assembly; they already comply). Publishing Common + bumping consumers off `local.props` is a release step (needs the feed/token).*

---

## 🔴 Priority 6 — highest leverage

### [ ] #28 · Front-End Testing & Quality — score 2 → 4 (weight 3)
The package ships reusable Blazor primitives with **no fast test tier**.
- **(medium)** No component tests for the UI library — `Tests/Presentation/MMCA.Common.UI.Tests` references only xUnit/Moq/AwesomeAssertions/coverlet (no bUnit), while `Source/Presentation/MMCA.Common.UI/Components/` ships ~12 components with real branching.
- **(low)** No axe/Lighthouse or visual-regression step in `ci.yml`; no a11y tooling in `Directory.Packages.props`.

**Fix**
- [ ] Add **bUnit**; write render/parameter/`EventCallback` tests, starting with the branching components (`MobileCardList`, `MobileInfiniteScrollList`).
- [ ] Wire **`Deque.AxeCore.Playwright`** into the existing E2E flows (≥1 a11y assertion).
- [ ] Run at least one **browser journey in MMCA.Common CI** so regressions in the shipped E2E helpers are caught here, not only downstream.

### [ ] #30 · Compliance, Privacy & Data Governance — score 1 → (lowest score, weight 2)
Soft-delete is the only deletion model — no lawful erasure path.
- **(medium)** `AuditableBaseEntity.Delete()` sets `IsDeleted=true`; global query filters everywhere; CLAUDE.md states entities are "never hard-deleted" — the exact GDPR/CCPA conflict the rubric names. `ExecuteDeleteAsync` exists but bypasses audit/events.
- **(low)** Processed outbox rows (serialized payloads, potential PII) are never purged — `OutboxProcessor` only sets `ProcessedOn`; ADR-003 itself notes the table "grows until cleaned up."
- **(low)** No PII/consent/DSR machinery (the AES-256-GCM `EncryptedStringConverter` is a real, shippable PII control to build on).

**Fix**
- [ ] Add an **`IAnonymizable` / erasure-orchestration seam** that reconciles soft-delete with subject deletion (anonymize-in-place, preserve audit trail).
- [ ] Add an **outbox-purge** background option with configurable retention.
- [ ] Write an **ADR** framing the soft-delete-vs-erasure tradeoff and the consumer's data-controller obligations.

---

## 🟠 Priority 3 — score 3, weight 3 (one rung from a 4)

### [ ] #29 · Resilience, Reliability & Business Continuity — 3 → 4
- **(medium)** `IntegrationEventConsumer` rethrows with a comment claiming "MassTransit will retry per its configured policy," but `ConfigureBrokerTransport` (`DependencyInjection.cs:403-431`) calls only `cfg.ConfigureEndpoints(context)` — **no** `UseMessageRetry`/`UseDelayedRedelivery`. Faulted messages dead-letter with zero retries on the extracted-microservice path the framework markets.
- *Gap:* no backup/restore, RTO/RPO, failover, SLOs, or chaos; resilience config is not test-enforced.

**Fix**
- [ ] Add a default **`UseMessageRetry` (backoff + jitter)** and **`UseDelayedRedelivery`** in `ConfigureBrokerTransport`; expose a hook for consumers to tune it.
- [ ] **Correct or remove** the misleading comment + log message.

### [ ] #32 · Dependency & Supply-Chain Management — 3 → 4 (weight 3, framework)
- **(medium)** The safety-critical **MassTransit v8 pin** (`Directory.Packages.props:28-36`) is guarded only by a prose comment; a blanket "update all" once bumped it to v9.1.2, which crashes every broker-enabled host at startup — and CI never starts a broker, so the build stays green. *(Matches the standing MassTransit-v8 constraint.)*
- **(low)** No lock files or SBOM for 11 published packages; no documented breaking-change/SemVer policy or CHANGELOG.

**Fix** *(the pin fix also closes #16's medium)*
- [ ] Replace the exact pin with a **constrained range** `[8.5.5,9.0.0)`, **or** add a fitness test asserting the MassTransit major stays ≤ 8.
- [ ] Enable **`RestorePackagesWithLockFile`** + commit lock files.
- [ ] Add a **CycloneDX SBOM** step to the release workflow.
- [ ] Publish a brief **versioning / breaking-change policy** + CHANGELOG.

### [ ] #11 · Security — 3 → 4 *(no confirmed red flags)*
- *Gap:* no explicit CI dependency-vuln gate or `<auditSources>` in nuget.config; no security fitness tests; insecure dev defaults (`requireHttpsMetadata=false`, permissive dev CORS); no SECURITY.md/threat model.

**Fix**
- [ ] Add a `dotnet list package --vulnerable` (or restore `--audit`) **CI gate**.
- [ ] Add **NetArchTest security invariants** (no stray `[AllowAnonymous]`; no `AllowAnyOrigin` + `AllowCredentials`).
- [ ] Commit a **SECURITY.md** with an OWASP Top-10 review note.

### [ ] #4 · Domain-Driven Design — 3 → 4 *(no confirmed red flags)*
- *Gap:* no DDD-specific fitness functions; minor factory inconsistencies (`UserNotification.Create` returns a bare entity; `Money.operator+` throws on currency mismatch).

**Fix**
- [ ] Add NetArchTest rules: aggregates expose **private ctors + factory methods**; **no cross-aggregate navigation properties**.
- [ ] Normalize the factory convention to **always return `Result<T>`**.

### [ ] #18 · UI Architecture & Component Design — 3 → 4 *(no confirmed red flags)*
- *Gap:* no bUnit/render tests; component conventions review-only.

**Fix**
- [ ] **(shared with #28)** add bUnit coverage for the primitives.
- [ ] Consider an analyzer/convention check for `EditorRequired` contracts on shared components.

### [ ] #19 · State Management & Data Flow — 3 → 4
- **(low)** `UnsavedChangesGuard` exposes `IsDirty` only as a `[Parameter]`; `HandleBeforeInternalNavigationAsync` reads it one render late, so clearing dirty + `NavigateTo` *without* an intervening `StateHasChanged()` still shows the dialog. `Source/Presentation/MMCA.Common.UI/Components/UnsavedChangesGuard.razor:24,38-55`. Untested. *(This is the known param-lag foot-gun.)*

**Fix**
- [ ] Add an optional **`Func<bool>?` live-accessor** parameter so the guard reads current dirty state at navigation time.
- [ ] Cover with a **bUnit test**.

---

## 🟡 Priority 2 — score 3, weight 2 (polish / hardening)

### [ ] #6 · CQRS & Event-Driven
- **(medium)** No consumer-side idempotency/inbox for at-least-once broker delivery — duplicate side effects possible in any non-idempotent consumer. **(low)** Same misleading "MassTransit will retry" comment.
- [ ] Ship an optional **EF-backed inbox/dedup filter** keyed on a message id; add a unique **event Id** to base events.

### [ ] #16 · Maintainability & Evolvability
- **(medium)** Blanket NuGet update reintroduced known-bad MassTransit v9 (commit `87d54ee`) — fixed by a comment, not a rule. **(low)** No CHANGELOG/breaking-change policy for 11 published packages.
- [ ] **Closed by the #32 pin fix** + add a per-release **CHANGELOG**.

### [ ] #13 · Observability & Operability
- **(low)** The outbox dead-letter Meter `MMCA.Common.Outbox` is created but no `AddMeter` call exists → the dead-letter counter is **never exported** (contradicts CLAUDE.md); mitigated by an Error-level log.
- [ ] Add **`AddMeter("MMCA.Common.Outbox")`** to `WithMetrics`; emit **RED histograms** for command/query latency.

### [ ] #17 · DevOps & Deployment
- **(low)** Security/audit only implicit (no Dependabot/CodeQL/audit step).
- [ ] Add **Dependabot** + an explicit audit job; push **`.snupkg`** symbol packages (currently built but never published).

### [ ] #9 · API & Contract Design
- **(low)** `ServiceContractAttribute` documents architecture-test enforcement that **does not exist**.
- [ ] Implement the **NetArchTest rule** (or remove the claim); add **OpenAPI generation + a contract snapshot test**.

### [ ] #20 · Design System & UI Consistency
- **(low)** Bootstrap chrome (NavMenu top bar/hamburger) coexists with MudBlazor in the shared package.
- [ ] Migrate remaining **Bootstrap chrome → MudBlazor**, drop the bundled Bootstrap CSS; source the brand hex from one token.

### [ ] #23 · Front-End Performance
- **(low)** `MobileInfiniteScrollList` appends every page into one `MudStack` with **no virtualization/cap**.
- [ ] Add **`Virtualize`** windowing or a rendered-item cap.

### [ ] #33 · Developer Experience & Inner Loop
- **(low)** The 11-package local-dev swap list is hand-maintained three times in each consumer's `Directory.Build.targets` and can silently drift.
- [ ] **Generate the list from a glob**, or add a smoke test that the `UseLocalMMCA` swap resolves all packages.

### [ ] #34 · Architecture Governance & Documentation
- **(low ×2)** `ArchitecturalAnalysis.md` contradicts the code on DB-per-service ("deliberately not database-per-service," race "only mitigated"); the two biggest recent decisions (DB-per-service, gRPC extraction) lack ADRs.
- [ ] Refresh the analysis doc; write the **two missing ADRs**; add an ADR index/template.

### [ ] #5 · Vertical Slice Architecture *(no confirmed red flags)*
- [ ] Add a **slice-cohesion NetArchTest** (a feature's command/handler/validator/mapper stay co-located).

### [ ] #12 · Performance & Scalability *(no confirmed red flags)*
- [ ] Add a **BenchmarkDotNet** smoke project; add a framework-level **max-page-size guard**.

### [ ] #31 · Cost Efficiency / FinOps *(no confirmed red flags)*
- [ ] Expose an **OTel sampler** knob; cap per-message Info logging volume.

---

## ✅ Already at level 4 — protect, don't regress
#1 SOLID · #2 Design Patterns · #3 Clean Architecture · #7 Microservices Readiness · #8 Data Architecture · #10 Cross-Cutting Concerns · #14 Testability · #15 Best Practices & Code Quality
*(All backed by fitness functions — the regression guard is keeping those tests green.)*

## ⚪ N/A for a framework (excluded from the index)
#21 Accessibility · #22 Responsive · #24 Forms/UX Safety · #25 Navigation · #26 Front-End Security · #27 i18n
*(Assessable only in consumer apps; #26 shared surface is covered under #11.)*

---

### Suggested sequencing
1. **MassTransit v8 fitness test** (#32 + #16) — one small test, closes two mediums, prevents a recurring prod crash.
2. **Broker retry policy** (#29 + #6) — the async path is the system's weakest seam.
3. **bUnit harness** (#28 + #18 + #19 guard) — unlocks the whole front-end tier.
4. **Erasure seam + outbox purge** (#30) — the only score-1 category; real compliance exposure.
5. Sweep the **fitness-function gaps** (#4, #11, #5) and **doc/CI hygiene** (#34, #17, #9, #13) as steady cleanup.
