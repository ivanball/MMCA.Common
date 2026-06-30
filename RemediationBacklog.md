# MMCA.Common — Architecture Remediation Backlog

Derived from `ArchitectureScorecard.md` (canonical two-axis scoring: **Maturity 91.7% / Implementation 84.1%**, framework v1.92.0).
The wave-by-wave priority ranking below is the **historical single-axis review** (index 80%, 218/272, 2026-06-08/09); it is retained for provenance and is **superseded by the in-repo two-axis scorecard**, which is the live source of scores.
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
- ✅ **#34 — governance.** Refreshed the stale DB-per-service passages in `Docs/Architecture/ArchitecturalAnalysis.md`; added **ADR-006** (database-per-service) + **ADR-007** (gRPC extraction) + **ADRs/README.md** index.
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

## Progress — v1.80.0 (2026-06-26)

> The single-axis backlog above is from the **2026-06-08/09** review (index 80%). The framework has since
> reached **v1.82.0** and the canonical scoring was the **in-repo, two-axis**
> [`ArchitectureScorecard.md`](ArchitectureScorecard.md)
> (**Maturity 92.2% / Implementation 82.9%** at that wave — current: **92.8% / 85.0%**, see the scorecard
> header). This entry records what shipped and the
> remaining framework-side follow-ups; it does not re-derive the single-axis priority ranking above.

- ✅ **#11 / #1 — permission-based authorization (opt-in).** `IPermissionRegistry`/`PermissionRegistryBuilder`
  (Shared) + `[HasPermission]`/`PermissionPolicyProvider`/`PermissionAuthorizationHandler` (API), wired via
  `AddAuthorizationPolicies` + `AddPermissions`, backward-compatible with the named role policies; 13 unit
  tests. Adopted by ADC (≈20 endpoints + `RoleNames.ContentEditor`). RBAC-with-capability-indirection
  (policy-based, not resource/attribute-based).
- ✅ **#14 / #1 — `TimeProvider` adoption.** Injected into `TokenService` (token `iat`/`nbf`/`exp`) and the
  notification read handlers; `UserNotification.MarkAsRead(DateTime readOnUtc)` now takes an explicit UTC
  timestamp. Registered `TimeProvider.System` singleton.
- ✅ **#34 — ADR-019 (layered rate limiting)** documents the pre-existing authenticated-only global limiter;
  ADRs 017/018 committed; ADR set now **001-019**.

**Framework-side follow-ups:**
- ✅ **Rate-limiter partition/exemption tests** (#11/§ADR-019). `IsRateLimitBypassed`/`GlobalRateLimitPartition`
  are now `internal` (via `InternalsVisibleTo`) and `RateLimitPartitionTests` covers the bypass paths,
  anonymous-vs-authenticated branching, and the per-user partition-key fallback (name → user_id → IP →
  constant). *(2026-06-26)*
- ✅ **Controlled-clock notification handler tests** (#14). Both mark-as-read handler tests now inject a
  fixed `TimeProvider` and assert the stamped `UserNotification.ReadOn`. *(2026-06-26)*
- ✅ **`BaseDomainEvent.DateOccurred` ambient clock — accepted as deliberate, not removed** (#4). A domain
  event's occurrence instant *is* the moment the aggregate raises it, so the creation-time default is the
  correct event-sourcing / audit semantic (and four domain tests enforce it). Relocating the stamp to the
  SaveChanges boundary would shift occurrence-time → persist-time and regress that semantic; threading a
  clock through every aggregate is disproportionate. Documented as a deliberate choice in
  `BaseDomainEvent` rather than changed. *(decision 2026-06-26)*

---

## Progress — v1.81.0/v1.82.0 + governance pass (2026-06-26)

> Released since v1.80.0 (v1.81.0, v1.82.0) plus a sixth governance pass currently **in flight (uncommitted)**.
> All of it lands in categories already scored 9-10, so the two-axis indices were unchanged at that wave
> (**Maturity 92.2% / Implementation 82.9%**; current: **92.8% / 85.0%**); these are evidence/governance
> enrichments, not score-movers.

- ✅ **#9 — Scalar OpenAPI UI (opt-in, released v1.81.0).** `MapCommonScalarUi()` renders `/scalar/{doc}`
  from the generated document, non-Production only, via the bundled `Scalar.AspNetCore 2.16.6` (no CDN).
  The committed-baseline drift gate stays deliberately consumer-owned (the API surface lives in the
  consumer hosts). §9 impl held at 9.
- ✅ **#31 — `COST.md` FinOps note (released v1.81.0).** Consolidates the framework's cost levers
  (telemetry poll-span filtering, outbox poll/retention tuning) and the right-sizing / attribution /
  surge-revert defaults consumers set. Doc enrichment; §31 impl held at 6 (execution is consumer/IaC).
- ✅ **#11 / #26 — RS256 pinned on the JWKS-forwarded auth path (v1.82.0).**
  `ValidAlgorithms = [RsaSha256]` on the forwarded-JWT validation path, matching the in-process pin.
- ✅ **#11 / #26 — security-response headers centralized (ADR-023, uncommitted pass).** One pluggable
  middleware in `MMCA.Common.Aspire.Security` (`AddCommonSecurityHeaders` + `ICspPolicyProvider` +
  `SecurityHeadersMiddleware`, unit-tested) replaces per-host hand-rolled headers. §11/§26 impl held at 9
  (default static CSP deliberately omits `script-src`/`style-src` until a host registers a provider).
- ✅ **#34 / #16 — FACTS.md now generated + CI drift-gated (uncommitted pass).** `build/facts` computes
  the framework facts from source; `ci.yml:27-28` runs `dotnet run --project build/facts -- . --check` as
  a drift gate, so version / package count / ADR range / fitness counts can no longer drift. The rubric
  (`ArchitectureEvaluationCriteria.md`) and `FACTS.md` are now version-controlled in-repo. ADR set now
  **001-023** (ADR-023 added). §34 impl held at 9 (residual: ArchitecturalAnalysis.md in the
  uncommittable workspace root; plus this pass is mid-commit).

**Open follow-up surfaced this cycle (governance hygiene, not a score-mover):**
- [x] **Commit the sixth governance pass** — ADR-023, the source-generated CI-drift-gated `FACTS.md` +
  `build/facts`, the in-repo rubric, and this two-axis scorecard all shipped in **v1.83.0** (`b9a6a28`),
  resolving the prior cycle's "ADR-023 uncommitted" §34 caveat. *(Done 2026-06-27.)*
- [~] **Backfill the CHANGELOG and commit the docs pass.** *Partly addressed, superseded by the
  v1.85.0 follow-up below:* a `[1.85.0]` CHANGELOG entry was added (commit `f224595`), but **v1.83.0 and
  v1.84.0 still have no release notes** and the v1.85.0 docs governance pass (ADRs 024/025/026, the
  `FACTS.md` ADR-count bump, ADR cross-links, this scorecard/backlog) is still uncommitted. Tracked now
  under "Progress — v1.85.0 → Open follow-up". *(§34, transient hygiene nit, effort S.)*

---

## Progress — v1.83.0/v1.84.0 (2026-06-27)

> Released since v1.82.0 (v1.83.0, v1.84.0) plus a docs-only governance pass currently **in flight
> (uncommitted)**. **One score moved at this wave**: §30 Implementation 7→8. The canonical scoring at
> v1.84.0 was **Maturity 92.2% / Implementation 83.1%** (was 82.9%) per the in-repo
> [`ArchitectureScorecard.md`](ArchitectureScorecard.md); the v1.85.0 eighth wave below then took it to
> **92.8% / 85.0%**.

- ✅ **#30 — `PiiRedactor` log-masking shipped (v1.84.0, score 7→8).** `Domain/Privacy/PiiRedactor.cs`
  masks every `[Pii]`-marked member (shallow, value-erasing, `[REDACTED]` token, per-type reflection
  cache) before an entity carrying personal data reaches a structured log or telemetry attribute —
  closing the §30 red flag the rubric names verbatim ("PII in logs/telemetry"), previously
  documented-but-missing. Covered by **7 `PiiRedactorTests`** (incl. "never emits the clear-text PII
  values"). §30 **maturity holds at 3**: DSAR/export endpoints, consent capture, the personal-data
  inventory, residency verification, and retention *execution* stay consumer-owned, and
  `PiiConventionTests` still passes vacuously in-repo (no PII-carrying type lives here; no fitness
  function forces types through the redactor).
- ✅ **#34 — sixth governance pass committed (v1.83.0).** ADR-023 (security-response headers), the
  source-generated CI-drift-gated `FACTS.md` + `build/facts`, the in-repo rubric, and the two-axis
  scorecard all shipped, resolving the prior "ADR-023 uncommitted" caveat. §34 holds at M4/I9.
- ✅ **#13 / #29 — warm-up / readiness subsystem documented (ADR-025).** `WarmupHostedService` +
  `WarmupReadinessGate` + `OpenIdConnectMetadataWarmupTask` (wired into `AddServiceDefaults`) gate
  `/health/ready` until startup warm-up runs, holding cold replicas out of rotation (gate opens even on
  task failure = availability over warmth, lazy-retry under ADR-009). **Enrichment, not a score move:**
  §13 holds at I9 and §29 holds at 3/7 because the subsystem ships **without unit tests** and the §29
  recovery gaps (restore drill, RTO/RPO, SLOs) are unchanged. *(See the new #29 follow-up below.)*
- ✅ **#6 — two-channel notifications documented (ADR-024).** The pre-existing SignalR-push + durable
  `UserNotification`-inbox seams (`IPushNotificationSender`/`INotificationRecipientProvider`, no-op
  defaults) are now formally recorded. §6 evidence enriched, no move.

**Framework-side follow-ups surfaced this cycle:**
- [x] **#29 — unit-test the warm-up/readiness subsystem.** *RESOLVED in the eighth wave (v1.85.0):*
  `Tests/Hosting/MMCA.Common.Aspire.Tests/Warmup/{WarmupReadinessGate,WarmupHostedService,WarmupReadinessHealthCheck}Tests.cs`
  now cover the gate latch/idempotency/thread-safety, the hosted service running each `IWarmupTask`
  once + opening the gate even on task failure, and the health-check transitions. This converted
  "warm-up exists" into "warm-up verified" and lifted §29 Implementation 7→8.

---

## Progress — v1.85.0 (eighth wave: under-8 Implementation remediation, 2026-06-27)

> The under-8 Implementation remediation (commit `78e5312`, **tag `v1.85.0`**, HEAD `7082a5f`) lifted
> every category scored Implementation < 8 with shipped, tested in-repo evidence, and additionally moved
> one maturity score. Re-verified against current source. Canonical scoring is now
> **Maturity 92.8% / Implementation 85.0%** per the in-repo
> [`ArchitectureScorecard.md`](ArchitectureScorecard.md) (was 92.2% / 83.1%). Full Release build clean
> (0 warnings); 1651 tests pass.

- ✅ **#5 — Vertical Slice: Implementation 7→8 AND maturity 3→4.** `ArchitectureRules.Slices.cs` +
  `SliceCohesionTestsBase` (shared `MMCA.Common.Testing.Architecture`, the 18th fitness base) + a Common
  `SliceCohesionTests` subclass fail the build if a use-case slice's handler/validator is stranded from
  its same-assembly command/query contract. Because this is **automatic CI enforcement of the slice
  convention**, §5 now meets the rubric's maturity-4 "enforced automatically by tests/CI" bar (like every
  other fitness-gated category) — the one maturity move this cycle. §5 moves to the level-4 protect list.
- ✅ **#12 — Performance: Implementation 7→8.** `Tests/Performance/MMCA.Common.Benchmarks` (BenchmarkDotNet
  smoke harness, outside the `.slnx`) makes hot-path spec efficiency *measured, not assumed*; the
  max-page-size guard already shipped at v1.84.0.
- ✅ **#17 — DevOps: Implementation 7→8.** Reference `samples/deployment/{foundation,main}.bicep`
  (Container Apps + ACR-via-managed-identity + Key Vault + SQL + cost tags + budget; lint clean via
  `az bicep build`) + `DEPLOYMENT.md` (OIDC federated-credential + UAMI bootstrap + smoke-gate/auto-rollback).
  Held at 8 — a library can't self-deploy; full CD-to-Azure lives in consumer repos.
- ✅ **#24 — Forms/Validation: Implementation 7→8.** Register/Login converted to `EditForm` +
  `DataAnnotationsValidator` + per-field `ValidationMessage` over typed `RegisterModel`/`LoginModel`
  (`PasswordComplexityAttribute` mirroring the server rule), closing the "errors not tied to the input"
  red flag; `AuthModelValidationTests` + `RegisterFormTests` cover it.
- ✅ **#25 — Navigation: Implementation 7→8.** In-shell `Pages/Forbidden.razor` (403) wired into
  `Routes.razor` (NotAuthorized→`<Forbidden/>`) + `NavigationFlow.md` documenting the Common UI route/role
  model; `ForbiddenTests` cover it.
- ✅ **#29 — Resilience: Implementation 7→8.** Warm-up subsystem now unit-tested (above) + `RESILIENCE.md`
  (baseline SLO/error-budget template + restore-drill runbook reference). Maturity held at 3 — the drill
  itself executes in consumer IaC; no in-repo measured RTO/RPO or SLO.
- ✅ **#31 — FinOps: Implementation 6→7.** OTel `Telemetry:TracesSampleRatio` →
  `ParentBasedSampler(TraceIdRatioBasedSampler)` knob (unit-tested, the biggest trace-ingestion lever) +
  outbox per-message log moved Information→Debug + `COST.md` cost-attribution-tag/cost-guard samples.
  Maturity held at 2 — right-sizing/attribution/reversible-scale is consumer/IaC.
- ✅ **#9 / #34 — `ServiceContractAttribute` doc-comment corrected.** It no longer claims a dedicated
  `[ServiceContract]` architecture test exists in each consumer solution; it now states the contract-purity
  invariant is upheld by the transport/layer-purity fitness rules (ADR-015) and that the attribute is an
  available documentation marker no contract type carries yet — closing the long-standing #9 "documents a
  test that doesn't exist" sub-item (§9 already impl 9, no score move).
- ✅ **#10 / #34 — ADR-026 (two-tier caching strategy) added.** Documents the `ICacheService` substrate
  (startup-time memory-or-distributed swap via `AddCaching`) + the HTTP output-cache edge, and the
  TTL-backstopped best-effort prefix invalidation — formalizing pre-existing §10 code (no score move).

**Open follow-up surfaced this cycle (governance hygiene, not a score-mover):**
- [ ] **Commit the v1.85.0 docs governance pass + backfill the CHANGELOG.** ADRs 024/025/026 are
  untracked, the `FACTS.md` ADR-count bump (23→26) + ADR-003/004/005/010/015 cross-links + the
  `ServiceContractAttribute` doc-fix are modified, and **this scorecard/backlog refresh** is uncommitted.
  The CHANGELOG now carries a `[1.85.0]` entry but still **lacks v1.83.0 and v1.84.0** sections (and
  `[Unreleased]` is empty), so those two releases have no notes. Add the 1.83.0 + 1.84.0 CHANGELOG
  sections and commit the docs pass so §34 traceability is consistent again. *(§34, transient hygiene
  nit, effort S.)*

---

## Progress — v1.86.0→v1.92.0 (ninth wave: i18n + re-score, 2026-06-29)

> Re-scored against current source at framework **v1.92.0** (HEAD `93ffcac`, dirty tree). Canonical scoring
> is now **Maturity 91.7% / Implementation 84.1%** (was 92.8% / 85.0%) per the in-repo
> [`ArchitectureScorecard.md`](ArchitectureScorecard.md). **Five scores moved**: one new category (§27),
> one offsetting maturity regression (§23), and three closer-evidence recalibrations (§11, §22, §30-reviewed).
> Both indices dip slightly — honest re-calibration plus a newly-scored immature category, not regressed work.

- ➕ **#27 — i18n flipped N/A → Maturity 2 / Implementation 6 (NEW open item).** Multi-locale i18n
  (en-US + Spanish) now ships *in the framework itself* (ADR-027, superseding the single-locale ADR-011):
  co-located `.resx` + `IStringLocalizer<T>`, edge error localization keyed by `Error.Code`, a culture
  cookie forwarded as `Accept-Language`, and `User.PreferredCulture`. The last N/A category is now scored,
  so all 34 count. *Gap (the freshest in-repo gap, weight 1, priority 2):* no missing-key/translation-coverage
  CI gate, no pseudo-localization pass, culture-less formatting guarded only by an advisory analyzer
  (`MA0076`). *(See the Priority-2 #27 item below.)* — `Shared/Globalization/SupportedCultures.cs:18`;
  `API/Localization/ErrorResourceSource.cs` + `*.es.resx`; `UI/Components/CultureSwitcher.razor`.
- 🔻 **#11 — Security Implementation 9→8 (recalibration; still Maturity 4).** "Strong", not "Exemplary":
  vault/managed-identity secret binding is deployer-owned and authz is RBAC-with-capability-indirection,
  not resource/attribute-based. *Enriched this wave (no further move):* ADR-032 PBKDF2-HMAC-SHA512 password
  hashing (`PasswordHasher.cs`, 600k iterations + legacy-salt migrate + `FixedTimeEquals`, 11 tests) and
  ADR-029 brute-force protection now documented.
- 🔻 **#22 — Responsive Implementation 8→7 (recalibration).** Cross-browser gate is chromium-only
  (firefox/webkit advisory), the 48px touch-target rule is cart-drawer-scoped, no density options. Already
  tracked consumer-assessed; no new item.
- 🔻 **#23 — Front-End Performance Maturity 4→3 (recalibration).** The patterns are convention/review-enforced,
  not automatically gated or measured (no Core Web Vitals/Lighthouse anywhere). Already an open Priority-2
  item (#23); the regression aligns the backlog with reality.
- ◐ **#29 — broker retry sub-items now CLOSE.** `ConfigureBrokerTransport` applies `cfg.UseMessageRetry`
  (exponential) on **both** RabbitMQ (`DependencyInjection.cs:432`) and Azure Service Bus (`:449`); the
  `IntegrationEventConsumer` comment + the doc-comment are corrected. The Priority-3 #29 descriptive text
  ("no `UseMessageRetry`") is **drifted** and corrected below. `UseDelayedRedelivery` stays deliberately
  omitted (`DependencyInjection.cs:408`, accepted). **Category #29 itself stays open at Maturity 3** on the
  unchanged recovery gaps (no in-repo RTO/RPO, drilled restore, SLOs).
- ◐ **#30 — PII erasure contract now gated; Maturity held at 3 (reviewed).** A new
  `PiiErasureContractFitnessTests` build gate forces a `[Pii]` `DataSubjectSample` through `PiiRedactor` +
  `IAnonymizable` (`Tests/Architecture/.../PiiErasureContractFitnessTests.cs:19-40`), closing the prior
  "vacuous PII guard" sub-item. **Maturity was reviewed and held at 3** (not lifted to 4): the gate verifies
  the erasure *mechanism*, but the structural `PiiConventionTests` scan is still vacuous (no PII-bearing type
  in Common's Domain) and the broad §30 governance (DSAR/consent/residency/retention/inventory) is
  consumer-resident. See the #30 clarification below.
- ✅ **Evidence enrichment, no score move:** ADR-028 day/dark theme (§20 — wired toggle, raw-hex/`!important`
  deductions hold), ADR-030 startup sole-migrator (§8/§17 — runtime self-migration, not the CI migration-apply
  gate those gaps name), ADR-031 feature-flag management (§10). ADR set grew 026→032; `FACTS.md` fitness
  counts advanced (71 methods/18 bases, Common runs 38).

**Open follow-up surfaced this cycle (governance hygiene, not a score-mover):**
- [ ] **Commit the v1.86.0→v1.92.0 docs/source pass.** ADR-032 is untracked; ADRs 001/007/008/017/020/022/030
  + `ADRs/README.md` + `FACTS.md` + one `WebApplicationExtensions.cs` source edit are modified; this
  scorecard/backlog refresh is uncommitted. Commit so §34 traceability is consistent again. *(§34, S.)*

---

## 🔴 Priority 6 — highest leverage

### [x] #28 · Front-End Testing & Quality — score 2 → 4 (weight 3) · *RESOLVED 2026-06-27*
The package ships reusable Blazor primitives with **no fast test tier**.
- ~~**(medium)** No component tests for the UI library~~ — **RESOLVED:** `Tests/Presentation/MMCA.Common.UI.Tests` references `bunit` (2.7.2) + the shipped `MMCA.Common.Testing.UI` harness and ships **29 component tests** across the branching primitives (`MobileCardList`, `MobileInfiniteScrollList` — empty/cards/cap/click/error+retry), `UnsavedChangesGuard`, `NotificationBell`, `DeleteConfirmation`, `PageStateScope`, `RedirectToLogin`, and the `PageHeader`/`PageLoadingState`/`PageErrorState` primitives (`PrimitivesTests`).
- ~~**(low)** No axe/Lighthouse or visual-regression step in `ci.yml`~~ — **RESOLVED:** `Deque.AxeCore.Playwright` (4.12.0) is pinned and shipped in `MMCA.Common.Testing.E2E` (`Page.AssertNoAccessibilityViolationsAsync()`); the `ui-e2e` CI job runs a **cross-browser matrix** (chromium required gate; firefox/webkit advisory) over the backend-less gallery with **6 axe-core WCAG 2.1 AA assertions** + render smoke.

**Fix**
- [x] Add **bUnit**; write render/parameter/`EventCallback` tests, starting with the branching components (`MobileCardList`, `MobileInfiniteScrollList`). → 29 component tests in `MMCA.Common.UI.Tests`.
- [x] Wire **`Deque.AxeCore.Playwright`** into the existing E2E flows (≥1 a11y assertion). → 6 axe assertions across Login/Register/Components/Notifications.
- [x] Run at least one **browser journey in MMCA.Common CI** so regressions in the shipped E2E helpers are caught here, not only downstream. → `ui-e2e` job (`.github/workflows/ci.yml`), gallery host self-served, chromium gate.

### [x] #30 · Compliance, Privacy & Data Governance — score 1 → 4 (weight 2) · *RESOLVED 2026-06-27*
> _Single-axis review only. In the live two-axis scorecard §30 is **Maturity 3 / Implementation 8** — the in-repo erasure mechanism is complete (and now fitness-gated, see the 2026-06-29 item below), but the broad governance process (DSAR/consent/residency/retention/inventory) is consumer-owned, so two-axis maturity is held at 3, not 4._

Soft-delete is the only deletion model — no lawful erasure path. *(All three fix items shipped; see the wave-1 progress entry above and the 2026-06-27 closeout below.)*
- ~~**(medium)** `AuditableBaseEntity.Delete()` sets `IsDeleted=true` … the exact GDPR/CCPA conflict the rubric names.~~ — **RESOLVED:** `IAnonymizable` erasure seam (`Domain/Interfaces/IAnonymizable.cs`), enforced by the `PiiConventionTests` fitness rule (a `[Pii]`-marked property obliges `IAnonymizable`); the AES-256-GCM `EncryptedStringConverter` ships for retrievable PII.
- ~~**(low)** Processed outbox rows … are never purged~~ — **RESOLVED:** `OutboxCleanupService` purges processed outbox (and inbox) rows older than `Outbox:RetentionDays` (default 7) from every relational source.
- ~~**(low)** No PII/consent/DSR machinery~~ — **RESOLVED (framework seam):** `[Pii]` marker + `PiiConventionTests` + `EncryptedStringConverter`, and now `PiiRedactor` masks `[Pii]` members before they reach a structured log / telemetry attribute (closing the documented-but-missing log-redaction half of the `[Pii]` contract). DSR/erasure *endpoints* remain consumer-owned (ADC ships them — see ADC #30).

**Fix**
- [x] Add an **`IAnonymizable` / erasure-orchestration seam** that reconciles soft-delete with subject deletion (anonymize-in-place, preserve audit trail). → `IAnonymizable` + ADR-005 + `PiiConventionTests` guard.
- [x] Add an **outbox-purge** background option with configurable retention. → `OutboxCleanupService` (`Outbox:RetentionDays`).
- [x] Write an **ADR** framing the soft-delete-vs-erasure tradeoff and the consumer's data-controller obligations. → `ADRs/005-soft-delete-vs-erasure.md`.
- [x] **(2026-06-27) Make the `[Pii]` log-masking real** — `PiiRedactor` (`Domain/Privacy/PiiRedactor.cs`) masks every `[Pii]`-marked member (shallow, value-erasing) so an entity carrying personal data can be logged without leaking clear-text PII; the `PiiAttribute` doc previously *advertised* this policy but no implementation existed. Covered by 7 `PiiRedactorTests`.
- [x] **(2026-06-29) Gate the erasure contract with a fitness function** — `PiiErasureContractFitnessTests` (`Tests/Architecture/.../PiiErasureContractFitnessTests.cs:19-40`) forces a `[Pii]`-marked `DataSubjectSample` through `PiiRedactor` + `IAnonymizable` end-to-end, so the redaction/erasure mechanism is no longer un-gated. *Note:* this verifies the **mechanism**; the repo-wide `PiiConventionTests` scan stays vacuous (no PII-bearing type lives in Common's Domain) and the DSAR/consent/residency/inventory **process** stays consumer-owned, so two-axis §30 maturity is held at 3.

---

## 🟠 Priority 3 — score 3, weight 3 (one rung from a 4)

### [ ] #29 · Resilience, Reliability & Business Continuity — 3 → 4
- ~~**(medium)** No broker retry policy on the extracted-microservice path~~ — **RESOLVED (re-verified 2026-06-29):** `ConfigureBrokerTransport` applies `cfg.UseMessageRetry` (exponential) on **both** RabbitMQ (`DependencyInjection.cs:432`) and Azure Service Bus (`:449`), and the `IntegrationEventConsumer` comment + log are corrected. `UseDelayedRedelivery` is deliberately omitted (`DependencyInjection.cs:408`, accepted — needs the RabbitMQ delayed-exchange plugin).
- *Gap (why #29 stays open at Maturity 3):* no in-repo backup/restore drill, RTO/RPO, failover, or SLOs. *(chaos/fault-injection covered — see below.)*

**Fix**
- [x] **Fault-injection / chaos test landed (C-8, 2026-06-19).** `ResilienceCircuitBreakerFaultInjectionTests` (Grpc.Tests) drives an always-failing dependency through the standard resilience handler and asserts the circuit breaker trips and short-circuits further calls; `OutboxProcessorTests.IntegrationEventPublishFailure_DegradesGracefully_BuffersForRedelivery` asserts the outbox buffers the event (retry++, left unprocessed) when the broker is unreachable instead of crashing the processor.
- [x] Add a default **`UseMessageRetry` (backoff + jitter)** in `ConfigureBrokerTransport`; expose a hook for consumers to tune it (`MessageBusSettings.RetryLimit`/`RetryMinIntervalSeconds`/`RetryMaxIntervalSeconds`). *(`UseDelayedRedelivery` deliberately omitted — accepted.)*
- [x] **Correct or remove** the misleading comment + log message. *(Done — `IntegrationEventConsumer.cs:59-60` + the doc-comments at `DependencyInjection.cs:401,408`.)*

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
- **(low ×2)** `Docs/Architecture/ArchitecturalAnalysis.md` contradicts the code on DB-per-service ("deliberately not database-per-service," race "only mitigated"); the two biggest recent decisions (DB-per-service, gRPC extraction) lack ADRs.
- [ ] Refresh the analysis doc; write the **two missing ADRs**; add an ADR index/template.

### [x] #5 · Vertical Slice Architecture — **DONE (eighth wave: impl 7→8 AND maturity 3→4)** → moved to the level-4 protect list
- [x] Slice-cohesion fitness function added: `ArchitectureRules.Slices.cs` + `SliceCohesionTestsBase` (shared package, the 18th fitness base) + Common/ADC subclasses — fails the build if a handler/validator is stranded from its same-assembly contract. Because this is automatic CI enforcement of the slice convention, §5 maturity also rose 3→4 (the rubric's maturity-4 "enforced automatically by tests/CI" bar), so §5 now belongs in "Already at level 4 — protect, don't regress" below.

### [x] #12 · Performance & Scalability — **DONE (eighth wave, impl 7→8)**
- [x] BenchmarkDotNet smoke project added (`Tests/Performance/MMCA.Common.Benchmarks`, outside the .slnx). Max-page-size guard already shipped at v1.84.0 (`ApplicationSettings.MaxPageSize` clamp + `EntityQueryPipeline.MaxUnboundedResultLimit`).

### [x] #31 · Cost Efficiency / FinOps — **DONE (eighth wave, impl 6→7)**
- [x] OTel sampler knob exposed (`Telemetry:TracesSampleRatio` → ParentBased/TraceIdRatio, unit-tested); outbox per-message "dispatched" log moved Info→Debug; `COST.md` gained cost-attribution-tag + cost-guard samples. (Reaching 8 needs consumer-side cost execution.)

### [x] #17 · DevOps & Deployment — **DONE (eighth wave, impl 7→8)**
- [x] In-repo reference deployment sample added: `samples/deployment/{foundation,main}.bicep` (lint clean via `az bicep build`) + `DEPLOYMENT.md` (OIDC federated-credential + UAMI bootstrap + smoke-gate/auto-rollback). (Deeper CD-to-Azure lives in consumer repos.)

### [x] #29 · Resilience & Business Continuity — **DONE (eighth wave, impl 7→8)**
- [x] Warm-up subsystem unit-tested (gate/hosted-service/health-check); `RESILIENCE.md` adds an in-repo SLO/error-budget template + restore-drill runbook reference. (The drill itself executes in consumer IaC — ADC's `dr-restore-drill.ps1`.)

---

## ✅ Already at level 4 — protect, don't regress
#1 SOLID · #2 Design Patterns · #3 Clean Architecture · **#5 Vertical Slice (new this cycle — maturity 3→4 on the slice-cohesion fitness function)** · #7 Microservices Readiness · #8 Data Architecture · #10 Cross-Cutting Concerns · #14 Testability · #15 Best Practices & Code Quality
*(All backed by fitness functions — the regression guard is keeping those tests green.)*

## ⚪ Mostly consumer-assessed (the shared Common.UI surface is scored here)
#21 Accessibility · #22 Responsive · #26 Front-End Security
*(Assessable mainly in consumer apps; #26 shared surface is covered under #11.)*
- **#27 i18n — no longer consumer-assessed/N/A.** It is now an active, in-repo scored category (Maturity 2 / Implementation 6) after ADR-027 shipped en-US + Spanish in the framework, superseding the single-locale ADR-011. Tracked as an open item in the ninth-wave progress section above.
- **#24 Forms/UX Safety — DONE for the shared surface (eighth wave, impl 7→8):** Register/Login are now `EditForm` + DataAnnotations + per-field `ValidationMessage` (typed models + `PasswordComplexity` attr + tests). Consumer module forms remain consumer-scored.
- **#25 Navigation — DONE for the shared surface (eighth wave, impl 7→8):** an in-shell `Forbidden` (403) page + `NavigationFlow.md` for the Common UI surface. Per-actor module flows remain consumer-scored.

---

### Suggested sequencing
1. **MassTransit v8 fitness test** (#32 + #16) — one small test, closes two mediums, prevents a recurring prod crash.
2. **Broker retry policy** (#29 + #6) — the async path is the system's weakest seam.
3. **bUnit harness** (#28 + #18 + #19 guard) — unlocks the whole front-end tier.
4. **Erasure seam + outbox purge** (#30) — the only score-1 category; real compliance exposure.
5. Sweep the **fitness-function gaps** (#4, #11, #5) and **doc/CI hygiene** (#34, #17, #9, #13) as steady cleanup.
