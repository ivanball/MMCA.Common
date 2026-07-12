# MMCA.Common — Architecture Remediation Backlog

Derived from `ArchitectureScorecard.md` (canonical two-axis scoring: **Maturity 94.4% / Implementation 83.7%**, framework v1.108.0; the 2026-07-09 seventeenth-wave two-pass re-score at a clean tree, HEAD `6c3b3bc`, recalibrated §21 Accessibility Implementation 9→8 (the shipped dark theme's two un-gated WCAG AA contrast failures are an unmet §21 color-and-contrast criterion, incompatible with the Exemplary band; the remediation is the open §20 dark-palette item), re-confirmed all 33 other categories at their prior scores, declined two adversarial first-pass lifts (§8 and §22 hold at Implementation 8), and refreshed the ADR range to 001-039).
The wave-by-wave priority ranking below is the **historical single-axis review** (index 80%, 218/272, 2026-06-08/09); it is retained for provenance and is **superseded by the in-repo two-axis scorecard**, which is the live source of scores.
Tasks are every applicable category scoring **< 4**, ranked by **priority = (4 − score) × weight**.
Higher priority = bigger weighted gap = more index points per unit of effort.

**Scope:** the single-axis item counts below are historical (the wave-by-wave ranking is superseded by the two-axis scorecard, per the note above). Under the live two-axis scorecard there are **no N/A categories** (§27 i18n is now scored after ADR-027) and **26 categories sit at Maturity 4** (#1, 2, 3, 4, 5, 6, 7, 8, 10, 11, 12, 13, 14, 15, 16, 18, 19, 20, 21, 24, 26, 27, 28, 29, 32, 34; **#27 joined 2026-07-03** on the fifteenth-wave i18n completion train, M4/I9); the open work is the **8 categories still below Maturity 4**. Ranked by two-axis priority = (4 − maturity) × weight: **#31** FinOps (Maturity 2, weight 2, computed priority 4) is the highest weighted gap but a documented accepted cap (see *Deliberate / accepted* below), not scheduled work; then the weight-2 Maturity-3 pack at **priority 2** (#9, #17, #22, #23, #25, #30, #33).

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
- [x] **Commit the v1.86.0→v1.92.0 docs/source pass.** **DONE 2026-06-30** (commit `5321aee`): ADR-032 +
  the modified ADRs 001/007/008/017/020/022/030 + `ADRs/README.md` + `FACTS.md` + the
  `WebApplicationExtensions.cs` rate-limiter-ordering edit committed; §34 traceability consistent again.

---

## Progress — tenth wave (focused in-repo remediation, 2026-06-30)

> Four scores moved **up** on shipped, tested in-repo evidence; both indices rose for the first time in
> several waves: **Maturity 91.7% → 92.9%** (301/324), **Implementation 84.1% → 84.9%** (688/810). Full
> Release build clean (0/0); 1670 unit/arch/bUnit tests + 12 chromium E2E pass. Commits `21fbdf9` (§27),
> `c04f456` (§29), `a28ce98` (§28), `fbb463b` (§21).

- ✅ **#27 — i18n: Maturity 2→3, Implementation 6→7.** Closes the two ADR-027 §7 follow-ups the scorecard
  named. (a) **Translation-coverage fitness gate:** `ResourceTranslationsAreComplete` (shared
  `MMCA.Common.Testing.Architecture`, the 19th fitness base) run as `LocalizationResourceTests` against
  `SupportedCultures.All` fails the build if any base `.resx` under `Source/` lacks a complete, non-empty
  sibling for a required culture, so coverage is verified not assumed. (b) **Culture-less formatting is now a
  build gate:** `MA0076` raised `suggestion`→`error` in `.editorconfig`; the 33 surfaced sites (validation
  messages, gRPC error details, UI log/notification text, tests) now use explicit `InvariantCulture`. ADR-027
  §7/§8 document both gates plus a locale-addition governance step. Held below M4: no pseudo-localization
  pass, only two locales.
- ✅ **#29 — Resilience: Maturity 3→4.** The in-repo restore drill (`DatabaseRestoreDrillTests`) runs on
  **every CI build** (a build gate in the unit tier, NOT a non-gating scheduled cron, which is the standard
  that keeps a scheduled drill at M3), and `RESILIENCE.md` now records the framework's **measured** restore
  baseline (~5 ms median RTO over 5 runs, 0-row RPO byte-for-byte asserted). The recovery procedure is thus
  demonstrated, measured, and automatically enforced in-repo, meeting the M4 bar. Implementation held at 8:
  production RTO/RPO against real cloud backups + measured production SLOs stay consumer IaC.
- ✅ **#28 — Front-End Testing: Implementation 8→9.** Closes "no visual-regression layer" with a
  **render-snapshot (golden-markup) regression** tier: `MarkupSnapshot` (shipped in `Testing.UI` for consumer
  reuse) normalizes per-render MudBlazor GUIDs and diffs shared-primitive markup against committed baselines
  (`PrimitivesSnapshotTests`, 5 baselines), failing the build on an unintended structural change. Deterministic
  and OS-independent (markup, not pixels), so it runs in the in-solution unit tier on every CI platform with
  no per-platform golden management (the Windows-dev-box-cannot-produce-Linux-CI-pixel-goldens constraint).
- ✅ **#21 — Accessibility: Implementation 8→9.** Broadened the chromium axe gate to the **loading
  (named progressbar) and error (alert) component states**, and added `ACCESSIBILITY.md` (documented manual
  screen-reader pass: landmarks/focus-order/ARIA-names/form-error association). Broadening the scan **found
  and fixed a real WCAG 4.1.2 defect**: `PageLoadingState` carried a prohibited `aria-label` on a bare `<div>`
  around an anonymous progressbar (now `role="status"` + a named spinner).

**Open follow-ups surfaced this wave:**
- [x] **#20 dark-mode palette contrast (Implementation, NEW).** The §21 dark-mode axe prototype found the
  dark palette's **filled-primary button label** and **error-alert message text** fail WCAG AA contrast
  (`PaletteDark.Primary`/`Error` paired with auto-computed text). Tracked here (documented in
  `ACCESSIBILITY.md`), deliberately NOT gated yet; tuning the dark palette is the remediation. *(§20, M.)*
  **RESOLVED 2026-07-11 (remediation wave 1):** `PaletteDark.PrimaryContrastText`/`ErrorContrastText`
  are now dark (`rgba(0,0,0,0.87)`, the Material dark-theme treatment, mirroring the standing
  `WarningContrastText` fix); the dark-mode axe scan is GATED (`DarkModeE2ETests` in the blocking
  chromium `ui-e2e` job: Login + Components re-scanned dark, reproduced both failures pre-fix, green
  post-fix). `ACCESSIBILITY.md` known-limitations updated. This is also the recorded §21 path back to
  Implementation 9 (re-score at the next cycle).
- [~] **Release done, sweep noted (deliberate).** The tenth wave was **released as `v1.93.0`** (git tag at
  HEAD `3e72bfa`; `FACTS.md` records it). Sweeping all 13 packages into ADC/Store/Helpdesk is the separate,
  cross-repo step and is **not verifiable from this repo** (memory records the sweep on 2026-06-30; confirm
  in each consumer's `Directory.Packages.props`). *(§16/§34.)*

---

## Progress — eleventh wave (ADR governance, 2026-06-30)

> **No score moves.** A full 34-category evidence re-score at framework **v1.93.0** (HEAD `3e72bfa`, dirty
> tree) re-confirmed every category at its tenth-wave value; indices hold at **Maturity 92.9% (301/324) /
> Implementation 84.9% (688/810)**. The wave records two pre-existing mechanisms as ADRs and syncs the
> scorecard/`FACTS.md` prose; no remediation lever moved.

- ✅ **ADR-033 + ADR-034 written (governance, no score move).** Both document mechanisms that **already ship
  in framework code**: ADR-033 (resource-ownership authorization) records the `OwnerOrAdminFilter` +
  `OwnershipHelper` axis (single-resource 403 on a `customer_id`-claim mismatch + an ownership `Specification`
  row-scoping collection queries, one admin bypass; opt-in, claim-trusting, not ABAC, Store-adopted), and
  ADR-034 (generic entity controllers + dynamic query contract) records the `EntityControllerBase` /
  `AggregateRootEntityControllerBase` generic REST surface + OData-lite query contract. ADR-033 is the
  resource-ownership criterion §11's Implementation-8 cap named, but it stays ownership-not-ABAC + opt-in, so
  **§11 correctly holds at I8** (watch-item, not a lever).
- ✅ **Scorecard + `FACTS.md` prose synced to ADR set 001-034** and **72 fitness methods / 19 bases** (Common
  runs 39); the stale §16/§34 ADR-count and Top-strength fitness/test counts were corrected.

**Open follow-up surfaced this wave (governance hygiene, not a score-mover):**
- [x] **#34: commit the ADR 033/034 docs pass.** The tree is dirty (ADRs 033/034 added; ADRs 015/026/030 +
  `ADRs/README.md` + `FACTS.md` modified) while the scorecard/backlog now reference 001-034, the recurring
  per-cycle traceability nit, resolves on commit. *(§34, effort S; #34 holds M4/I9.)* **RESOLVED 2026-07-03
  (fourteenth wave):** the working tree is clean at v1.101.0 (HEAD `5e55be2`), ADRs through 036 are committed,
  and `FACTS.md` matches the tag.

## Progress — twelfth wave (under-8 Implementation lift, v1.94.0 pending, 2026-06-30)

> **Two Implementation scores move up**, Maturity holds: **Implementation 84.9% → 85.3% (691/810)**,
> **Maturity 92.9% (301/324)** unchanged. Full Release build clean, **1685 tests pass**. Held for review
> at this writing (v1.94.0 not yet tagged), so the tree is dirty against v1.93.0.

- [x] **#22 · Responsive & Cross-Browser — Implementation 7→8.** Closes the two execution gaps the prior 7
  named: (a) **grid density options** now ship on `DataGridListPageBase` (`DenseGrid` + `ToggleDensity()`,
  round-tripped through `ListPageState` / URL key `d` / sessionStorage, unit-tested in
  `ListPageStateServiceTests` + `ListPageQueryStateServiceTests`); (b) the **48px touch target is generalized**
  from the cart-drawer-only rule into a shared `.mmca-touch-target` affordance (cart drawer + mobile cards +
  data-grid pager), enforced by a phone-viewport Playwright bounding-box test (passes locally, 13/13 UI E2E);
  (c) a **`RESPONSIVE.md`** device/breakpoint/browser matrix is documented (closing the "matrix implicit" note),
  referenced from `CLAUDE.md`. *Maturity held at 3: firefox/webkit still advisory (chromium-only blocking gate).*
- [x] **#27 · Internationalization · Implementation 7→8.** A real **pseudo-localization pass** ships:
  `PseudoLocalizer.Transform` (accents every letter, ~40% padding, bracket sentinel, preserves `{0}`
  placeholders) applied by a `PseudoStringLocalizer`/`PseudoStringLocalizerFactory` decorator over
  `IStringLocalizerFactory` (registered in `AddUIShared`, inert unless the pseudo culture is active),
  activated by a **Development-only `qps-Ploc` culture** wired into `UseCommonRequestLocalization` +
  `MapCultureEndpoint` + the `CultureSwitcher`, with `SupportedCultures.PseudoLocale` deliberately kept out of
  `All` so the translation-completeness gate is unaffected. Unit-tested in `PseudoLocalizationTests`. Closes
  the pseudo-localization gap the prior 7 named. *Maturity held at 3: only two locales ship and the pseudo
  pass is a dev diagnostic, not a CI gate.*
- [x] **#27 · Internationalization · Maturity 3→4 + Implementation 8→9 (fifteenth wave, 2026-07-03, i18n
  completion train; ADR-027 Decision 9).** Both remaining holds are closed with CI-enforced evidence:
  (a) the pseudo pass is now a REQUIRED CI gate (`PseudoLocalizationE2ETests` in the blocking chromium
  `ui-e2e` job: `[!!` sentinel round-trip + no horizontal overflow under ~40% expansion on `/login`,
  `/register`, `/components`, plus an `en-US` leak guard; the gallery host enables `qps-Ploc`
  unconditionally as unpackaged test infrastructure); (b) a second fitness gate
  (`LocalizedTextConventionTests`, subclassing the new shared `LocalizedTextConventionTestsBase`) fails
  the build on hard-coded snackbar/title/`<PageTitle>`/breadcrumb/`NavItem` literals. Implementation 9:
  MudBlazor chrome localized (`ResxMudLocalizer` + `MudTranslations.{resx,es.resx}`, all 145 built-in
  keys en+es, DI-resolution-tested); the framework's own chrome fully externalized (`SharedResource` 22→136
  keys: NavMenu, auth pages, error/empty/loading states, ReconnectModal, notification pages, UI.Web SSR
  Error page); `ErrorMessages.Success` fragment concatenation `[Obsolete]` (whole-sentence page keys);
  `Common.Error.*` no longer surfaces raw `ex.Message`; `NavItem.TitleResource` culture-aware nav;
  `LocalizationResourceTests` non-vacuous floor (`MinimumBaseResources = 3`). *Held below 10: two
  locales, no RTL. §27 joins the protect set at M4/I9.*
- [accepted] **#31 · Cost Efficiency / FinOps — Implementation deliberately capped at 7 (not chased).** A
  documented structural acceptance, not an open lever: the two unmet §31 criteria — **right-sizing** and
  **reversible scale-events** — are consumer/IaC execution a NuGet library provisions nothing to perform, and
  **per-service cost attribution** via Aspire resource annotations is inert for the hand-written-`main.bicep`
  consumers (ADC/Store), so even the one library-addressable criterion does not move the score for the actual
  consumers. The in-repo levers are already shipped (`Telemetry:TracesSampleRatio` sampler, outbox-log trim to
  Debug, `COST.md` attribution-tag + cost-guard samples). Further movement is a consumer-side lift, not an
  in-repo one. *(§31 holds M2/I7; see `COST.md` and the §31 scorecard row.)*

**Deferred follow-up (recorded, not done this wave):**
- [~] **#22: promote firefox (then webkit) from advisory to a blocking cross-browser gate** once observed
  reliably green, to lift §22 Maturity 3→4. *(`ci.yml:89`; effort S, gated on a green streak.)*
  **FIREFOX PROMOTED 2026-07-12 (remediation wave 5):** `ui-e2e`'s `continue-on-error` now exempts only
  webkit; firefox is a required merge gate alongside chromium (observed clean over the recent main-run
  streak). **Webkit stays advisory** (2 flaky reds in its last 10 main runs, 2026-07-11 09:59 and
  2026-07-12 00:45); promote it once it holds a comparable streak. §22 Maturity 3→4 candidacy recorded
  for the next re-score (the recorded lever named the firefox promotion as the move, with webkit staged).

---

## Progress - fourteenth wave (clean-tree evidence re-score at v1.101.0, 2026-07-03)

> A full 34-category, two-pass evidence re-score (per-category scorer plus adversarial verifier) at framework
> **v1.101.0** (HEAD `5e55be2`, working tree **clean**: the recurring uncommitted-docs caveat is closed).
> **Two scores moved.** Canonical scoring is now **Maturity 94.1% (305/324) / Implementation 83.6% (677/810)**
> per the in-repo [`ArchitectureScorecard.md`](ArchitectureScorecard.md) (was 93.5% / 84.0%). Four further
> first-pass downgrade proposals were not applied: §25 (I8 to 7) and §34 (M4/I8 to M3/I7) were refuted by the
> adversarial verifier, and §7/§9/§13 (each I9 to 8, band recalibrations) were declined on review (kept at I9);
> those rows received evidence corrections only.

- ✅ **#24 · Forms, Validation & UX Safety: Maturity 3→4 (CLOSED, moved to the level-4 protect list).** The
  shared auth-form convention is enforced automatically in the CI-gated `.slnx` unit tier:
  `Tests/Presentation/MMCA.Common.UI.Tests/Pages/Auth/AuthModelValidationTests.cs` (8 facts:
  required/email/complexity/password-match) plus `RegisterFormTests.cs` (the per-field `ValidationMessage`
  renders on an empty submit and the auth service is never invoked), meeting the rubric M4 "enforced
  automatically by tests/CI" bar, consistent with §19's M4 on the same CI-gated guard tests. *Caveat recorded:
  the shared `FormsConventionTestsBase` (`Testing.Architecture/Bases/FormsConventionTestsBase.cs:41,51`) is
  consumer-scoped (it enumerates create forms under `Source/Modules`, absent in Common, and no Common subclass
  exists), so the in-repo M4 rests on the bUnit/model tests, not that fitness base.*
- 🔻 **#14 · Testability: Implementation 9→8 (band recalibration, no work regressed).** The row's own stated
  reasoning (a modest 53% gated coverage floor, no mutation testing) describes the rubric's Strong band (7-8)
  while 9 sits in Exemplary, so 8 is the internally-consistent value. Maturity holds at 4. Path back to 9:
  ratchet the coverage floor upward and add mutation testing on the Core tier.
- ✅ **#34 follow-up (commit the ADR 033/034 docs pass) RESOLVED.** Clean tree at v1.101.0 (HEAD `5e55be2`);
  ADRs through 036 committed, `FACTS.md` matches the tag. Ticked in the eleventh-wave section above.
- ◐ **Evidence-cell corrections, scores unchanged:** §7 (the extraction rule body lives in the shared
  `Testing.Architecture` package: `ArchitectureRules.Transport.cs:19` plus `Bases/MicroserviceExtractionTestsBase.cs:13`,
  subclassed at `MicroserviceExtractionTests.cs:10`); §9 (`Scalar.AspNetCore` is 2.16.7 at
  `Directory.Packages.props:24`, not 2.16.6 at `:17`); §13 (drifted `Aspire/Extensions.cs` anchors re-pointed
  to `:37/:92/:147-148/:161/:264/:268/:277/:306/:314`, and the stale "warm-up ships without unit tests"
  hold-reason removed: 9 warm-up tests have existed since the eighth wave, so the hold at I9 rests solely on
  deployer-owned SLO alerting/dashboards/runbooks).

## Progress - defect-fix wave C-1..C-7 (2026-07-05)

Seven approved defect fixes, each behavior change landed with its pinning test flipped (or a new
regression test) in the same change; build 0/0 and the full `.slnx` suite green. One new test-only
package: `Microsoft.Extensions.TimeProvider.Testing` 10.7.0.

- ✅ **C-1 (security, §11)** `LoginProtectionService`: clamped the exponential-backoff shift exponent
  (excess >= 31 formerly yielded negative or wrapped lockout TTLs); backoff theory extended with deep rows.
- ✅ **C-2** `OAuthControllerBase.CompleteAsync`: safe `returnUrl` lookup with `/` fallback instead of
  `KeyNotFoundException` when the ticket lacks the item; regression test added.
- ✅ **C-3 (§13)** `LoggingQueryDecorator`: business failures now record `outcome=failed` on
  `cqrs.query.duration` plus a warning log (parity with the command decorator); the pin documenting
  the old asymmetry as intentional was flipped.
- ✅ **C-4 (BREAKING)** `ChildEntityServiceBase`: derives from `AuthenticatedServiceBase` and attaches
  the Bearer token on POST/DELETE; ctor now requires `ITokenStorageService` (consumer subclasses must
  pass it in the release sweep).
- ✅ **C-5** `EntityServiceBase.GetAllForLookupAsync`: `nameProperty` now `Uri.EscapeDataString`-escaped;
  escape-needing test added.
- ✅ **C-6** `OutboxCleanupService`: optional trailing `TimeProvider` ctor param (defaults to System);
  the purge sweep is now deterministically unit-tested with `FakeTimeProvider` over in-memory SQLite
  (old processed rows purged, newer/pending survive, per-source error isolation, `EnableInbox` gate).
- ✅ **C-7** `SessionCookieAuthenticationHandler`: expiry check moved from `DateTime.UtcNow` to the base
  handler's `TimeProvider`; deterministic fake-clock expiry test added.

---

## Progress: sixteenth wave (clean-tree re-score at v1.106.0, 2026-07-06)

> A full 34-category, two-pass evidence re-score (per-category scorer plus adversarial verifier) at framework
> **v1.106.0** (HEAD `6f8b917`, one commit past the v1.106.0 tag, working tree **clean**). **One score moved.**
> Canonical scoring is now **Maturity 94.4% (306/324) / Implementation 84.1% (681/810)** per the in-repo
> [`ArchitectureScorecard.md`](ArchitectureScorecard.md) (was 94.4% / 83.7%).

- ✅ **#14 · Testability & Test Strategy: Implementation 8→9.** The fourteenth wave capped §14 at 8 on
  "a modest 53% gated coverage floor"; that floor was ratcheted **53.0 → 68.3** (`.github/workflows/ci.yml:226`,
  commit `b75fa8f`, measured ~70.3%) and the suite grew from 1586 to **1880** `[Fact]`/`[Theory]` across
  **262** files via a coverage-driven program that found and fixed **seven real defects** (C-1..C-7, `55f3cab`),
  so the top band is now supported. Maturity holds at 4 (CI-gated fitness tests, the blocking coverage-floor
  gate, and the zero-discovery min-tests guard). Held below 10 by the one remaining Exemplary gap: no mutation
  testing on the Core tier. §14 stays on the level-4 protect list.
- ◐ **#34 · Architecture Governance & Docs: held at M4/I8 (adversarial 8→7 declined).** The re-score's
  adversarial pass proposed docking §34 to Implementation 7 because the scorecard's own prose was stale against
  the CI-gated `FACTS.md` (it self-dated v1.101.0 / ADRs 001-036 while `FACTS.md` reports v1.106.0 / 001-038).
  That staleness is cured by this very refresh (the rewritten scorecard is current at commit time); the durable
  Strong-8 cap remains the uncommittable `ArchitecturalAnalysis.md` in the workspace root. Evidence refreshed to
  ADR set **001-038** and **78 fitness methods / 25 bases (Common runs 40)**.
- ◐ **Evidence enrichment, no score move:** ADR-037 (field-level encryption at rest: `EncryptedStringConverter`
  ships but is explicitly latent/unadopted, §11/§30) and ADR-038 (supply-chain provenance: records the SBOM as
  generated-not-yet-signed, §32) land in categories already at 8-9; the C-1..C-7 defect fixes (§11 backoff-overflow
  clamp, §13 query-failure RED-metric parity) tighten existing mechanisms without moving a band.

**Doc-hygiene follow-ups surfaced this wave (outside the scorecard/backlog, not score-movers):**
- [ ] **`SECURITY.md:5` still says "thirteen packages"** (should be fourteen) and **`GETTING-STARTED.md`'s
  `Directory.Packages.props` sample lists 13 package entries plus a stale `1.77.0` example version.** Minor
  §34-adjacent staleness against the CI-gated `FACTS.md` (14 packages); refresh in a docs pass. *(§34, effort S.)*

---

## Progress - seventeenth wave (evidence re-score at v1.108.0, 2026-07-09)

> A full 34-category, two-pass evidence re-score (per-category scorer plus adversarial verifier) at framework
> **v1.108.0** (git HEAD `6c3b3bc`, working tree clean, one commit ahead of origin: the ADR-012 mixed-endpoint
> amendment awaiting push) moves one score and closes nothing: no open item below is proven shipped this run,
> and no below-Maturity-4 category reached M4, so the priority ranking is unchanged (#31 at computed priority 4,
> the documented accepted cap; the seven weight-2 Maturity-3 categories at priority 2).

- ◐ **#21 · Accessibility: Implementation recalibrated 9→8 (Maturity holds 4).** The tenth-wave 9 sat in the
  Exemplary band while the shipped, user-toggleable dark theme carries two documented, deliberately un-gated
  WCAG AA contrast failures (filled-primary button label + error-alert text, `ACCESSIBILITY.md` known
  limitations), a directly unmet §21 color-and-contrast criterion. With the standing smaller gaps (no automated
  focus-trap/reading-order assertion, manual pass only; axe breadth scoped to the gallery's representative
  states) the honest band is Strong; held at 8, not 7, because the gaps read as one minor cluster whose
  contrast half is a single palette-tuning item **already tracked as the open §20 follow-up above** (no
  duplicate item added; tuning the dark palette and gating the dark-mode axe scan is also the path back to 9).
- ◐ **#8 · Data Architecture: held at M4/I8 (adversarial 8→9 declined).** The first pass proposed an
  Implementation lift; refuted because the rubric's §8 "migrations run in CI/CD" criterion stays structurally
  unmet in-repo (`ci.yml` has no migration-apply step; ADR-030 boot-time self-migration is a runtime mechanism,
  not that gate), so the scorecard's standing "Held at 8 (not lifted)" note stands.
- ◐ **#22 · Responsive & Cross-Browser: held at M3/I8 (adversarial 8→9 declined).** No repo change since the
  twelfth-wave 7→8 lift; firefox/webkit remain advisory `continue-on-error` (`ci.yml:89`). The open follow-up
  above (promote firefox to blocking, +1 Maturity) is still the sole §22 lift.
- ◐ **Evidence enrichment, no score move:** ADR-039 (live channel push: ephemeral events over the notification
  hub via `ILiveChannelPublisher`) and the ADR-012 mixed-endpoint transport-profile amendment land in
  categories already scored 8-9; the ADR range refreshes to **001-039** (`FACTS.md`: 78 fitness methods /
  25 bases, Common runs 41).

## Progress - eighteenth wave (runtime performance wave, 2026-07-10)

> A cross-repo runtime-performance audit (4 parallel auditors: framework, ADC, Store, hosting/config)
> found the framework strong on read-path fundamentals (no-tracking, SQL pagination, batched populators,
> pipeline split-query, outbox smart-wait) but flagged a cluster of hot-path costs, all fixed this wave
> (details in `CHANGELOG.md` [Unreleased] and ADR-040). Mostly §12 Performance & Scalability plus §26
> caching evidence:

- ✅ **§12 · Outbox mark-processed set-based + async** (`ExecuteUpdateAsync`; was a nested synchronous
  `SaveChanges()` blocking a thread-pool thread per event-raising command); `InProcessEventBus` batch
  publish = 1 save + 1 update (was 2 round trips per event).
- ✅ **§12/§26 · ADR-040 `PublicEndpointOutputCachePolicy`**: authenticated requests no longer bypass the
  output cache on `[AllowAnonymous]` user-independent GETs (the UI's Bearer-on-every-request made the
  whole output-cache layer serve 0% of logged-in traffic).
- ✅ **§26 · Query cache hardening**: stampede protection in `CachingQueryDecorator` (per-key double-check
  locking); `Result`/`Result<T>` JSON round-trip converter (a Redis cache hit previously could not
  rehydrate: latent production incident once Redis appears); batched prefix invalidation (512-key
  deletes); single-copy serialization.
- ✅ **§12 · Retry ownership**: standard resilience handler capped at 1 retry (UI policy owns user-facing
  retries; stacked budgets amplified brownouts up to 16x); gRPC client resilience unified with the Aspire
  values via new `HttpResilienceDefaults` (Shared) + restored `PooledConnectionLifetime`/keep-alive.
- ✅ **§12 · Allocation/reflection batch**: lazy `Result` error list + shared success instance; typed-DTO
  list responses skip per-row `ExpandoObject` shaping when no `fields` requested (BREAKING: query-service
  generics widened to `object`; wire format unchanged); dispatcher closed-type cache; compiled failure
  factory; `Type.GetType` cache; `LocalView.FindEntry`; split-query heuristic in
  `EFReadRepository.ApplyIncludes`; command started-log to Debug + source-generated scope; gzip Fastest.
- ⏸ **Deferred with rationale**: interceptor `DetectChanges` reduction (the second detection pass may be
  load-bearing for audit stamps; needs a dedicated EF-internals investigation; silent-data-loss failure
  mode) and a by-id fast path around the dynamic query pipeline (larger refactor; pressure mostly removed
  by ADR-040).

## Progress - remediation wave 1 (cross-repo wave plan, 2026-07-11)

> First wave of the 2026-07-11 cross-repo remediation plan (workspace plan file). Ships the shared
> §18/§19 fitness bases the ADC/Store maturity lifts need, closes the tenth-wave #20 dark-palette item,
> and adds a §23 measurement gate. Full Release build 0/0; 2223 tests green; gallery E2E 21/21
> (19 prior + 2 dark-mode) plus 2 new vitals tests.

- ✅ **§18/§19 shared fitness bases (the ADC/Store maturity 3→4 levers, consumed on the next sweep).**
  `UIArchitectureConventionTestsBase` (code-behind 400-line cap + inline `@code` 120-line cap,
  non-vacuity guard) and `StateManagementConventionTestsBase` (no mutable static state in `Layer.Ui`
  assemblies via reflection, `AllowedStaticMembers` for recorded exceptions; plus a no-singleton
  `*StateService`/`*StateContainer` source scan). Both subclassed in-repo (dog-food): the §19 gate
  caught and fixed two real §18 violations (`MobileInfiniteScrollList` ~205 and `NotificationBell`
  ~135 inline `@code` lines, both split to code-behind partials, snapshots/bUnit green) and surfaced
  `ErrorMessages._localizer` (recorded as the one allowed static: write-once wiring seam, ADR-027).
- ✅ **#20 dark-mode palette contrast RESOLVED + GATED (§20/§21).** Dark `PrimaryContrastText`/
  `ErrorContrastText` now `rgba(0,0,0,0.87)`; `DarkModeE2ETests` (Login + Components, dark palette via
  the `mmca_theme` cookie) reproduced both documented AA failures pre-fix and now gates them in the
  blocking chromium `ui-e2e` job. §21 Implementation 8→9 candidacy recorded for the next re-score.
- ✅ **§23 measurement gate.** `WebVitalsE2ETests` asserts LCP/TTFB/CLS budgets on the gallery Login +
  Components pages (shipped `WebVitalsCollector`) inside the blocking `ui-e2e` job, so the shared-chrome
  front-end performance conventions are now measured AND enforced (the two gaps the §23 maturity-3
  recalibration named). §23 maturity 3→4 candidacy recorded for the next re-score.
- ✅ **§34 hygiene.** `GETTING-STARTED.md` no longer restates the current consumer version (links
  `FACTS.md`; sample version marked illustrative). *Noted for a future docs pass:* `CHANGELOG.md`'s
  `[Unreleased]` section still accumulates content shipped in v1.86.0 through v1.114.0 without
  per-release headings.

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

### [x] #29 · Resilience, Reliability & Business Continuity — 3 → 4 · *RESOLVED 2026-06-30 (tenth wave) — now on the level-4 protect list*
- ~~**(medium)** No broker retry policy on the extracted-microservice path~~ — **RESOLVED (re-verified 2026-06-29):** `ConfigureBrokerTransport` applies `cfg.UseMessageRetry` (exponential) on **both** RabbitMQ (`DependencyInjection.cs:432`) and Azure Service Bus (`:449`), and the `IntegrationEventConsumer` comment + log are corrected. `UseDelayedRedelivery` is deliberately omitted (`DependencyInjection.cs:408`, accepted — needs the RabbitMQ delayed-exchange plugin).
- ~~*Gap (why #29 stays open at Maturity 3):* no in-repo backup/restore drill, RTO/RPO, failover, or SLOs.~~ — **CLOSED (tenth wave, Maturity 3→4):** the in-repo `DatabaseRestoreDrillTests` runs as a **build gate on every CI build** (seed→backup→catastrophic-wipe→restore→verify on ephemeral SQLite; 0-row RPO byte-for-byte asserted), and `RESILIENCE.md` records the measured restore baseline (~5 ms median RTO over 5 runs), meeting the M4 "enforced automatically" bar. Production RTO/RPO against real cloud backups + measured prod SLOs stay consumer IaC, so Implementation is held at 8. *(chaos/fault-injection covered below.)*

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

### [x] #12 · Performance & Scalability: **DONE (eighth wave impl 7→8; Maturity 3→4 on the `performance-smoke` CI gate, 2026-07-02), moved to the level-4 protect list below**
- [x] BenchmarkDotNet smoke project added (`Tests/Performance/MMCA.Common.Benchmarks`, outside the .slnx). Max-page-size guard already shipped at v1.84.0 (`ApplicationSettings.MaxPageSize` clamp + `EntityQueryPipeline.MaxUnboundedResultLimit`).
- [x] **Maturity 3→4 via the build-gating `performance-smoke` job.** `.github/workflows/ci.yml:143-167` runs the BenchmarkDotNet harness (`--filter "*" --job Dry`) on every push/PR with no `continue-on-error`, so a benchmarked hot path (Specification compiled-expression cache plus criteria composition) that throws or no longer compiles fails the build. That automatic CI enforcement meets the rubric maturity-4 "enforced automatically by tests/CI" bar (`ci.yml:143-148`). It is a runs-clean smoke gate, **not** a latency-regression gate (`ci.yml:146-147`), so Implementation holds at 8.

### [x] #17 · DevOps & Deployment: **DONE (eighth wave, impl 7→8)**
- [x] In-repo reference deployment sample added: `samples/deployment/{foundation,main}.bicep` (lint clean via `az bicep build`) + `DEPLOYMENT.md` (OIDC federated-credential + UAMI bootstrap + smoke-gate/auto-rollback). (Deeper CD-to-Azure lives in consumer repos.)

### [x] #29 · Resilience & Business Continuity — **DONE (eighth wave, impl 7→8)**
- [x] Warm-up subsystem unit-tested (gate/hosted-service/health-check); `RESILIENCE.md` adds an in-repo SLO/error-budget template + restore-drill runbook reference. (The drill itself executes in consumer IaC — ADC's `dr-restore-drill.ps1`.)

---

## ✅ Already at level 4 — protect, don't regress
#1 SOLID · #2 Design Patterns · #3 Clean Architecture · **#5 Vertical Slice (maturity 3→4 on the slice-cohesion fitness function)** · #7 Microservices Readiness · #8 Data Architecture · #10 Cross-Cutting Concerns · **#12 Performance & Scalability (maturity 3→4 on the build-gating `performance-smoke` CI job, 2026-07-02)** · #14 Testability · #15 Best Practices & Code Quality · **#24 Forms, Validation & UX Safety (maturity 3→4 on the CI-gated auth-form tests, fourteenth wave)** · **#27 i18n (maturity 3→4 on the fifteenth-wave completion train, 2026-07-03)** · **#29 Resilience (maturity 3→4 on the build-gated restore drill, tenth wave)**
*(All backed by fitness functions: the regression guard is keeping those tests green. This lists the categories that reached Maturity 4 through tracked remediation; under the live two-axis scorecard the full Maturity-4 set is 26 categories, see the Scope note at the top.)*

## 🔒 Deliberate / accepted (documented caps, not scheduled work)
### [accepted] #31 · Cost Efficiency / FinOps: held at Maturity 2 / Implementation 7 by documented acceptance
Moved out of the active priority queue on 2026-07-02 (user-approved). Its computed priority = (4 − 2) × 2 = **4** is the highest weighted gap of any open category, but the unmet §31 criteria are consumer/IaC execution a NuGet library cannot perform: **right-sizing** and **reversible scale-events** are host-infrastructure actions the framework provisions nothing to take, and **per-service cost attribution** via Aspire resource annotations is inert for the hand-written `main.bicep` consumers (ADC/Store), so even the one library-addressable criterion does not move the score for the actual consumers. The in-repo levers are already shipped and documented: the `Telemetry:TracesSampleRatio` OTel sampler knob, the outbox per-message log trimmed Information→Debug, and `COST.md`'s cost-attribution-tag plus cost-guard samples. Further movement is a consumer-side lift, not an in-repo one, so §31 is recorded here as an accepted cap rather than scheduled work. *(See `COST.md`, the §31 scorecard row, and the twelfth-wave `[accepted]` note above for provenance.)*

## ⚪ Mostly consumer-assessed (the shared Common.UI surface is scored here)
#21 Accessibility · #22 Responsive · #26 Front-End Security
*(Assessable mainly in consumer apps; #26 shared surface is covered under #11.)*
- **#27 i18n: CLOSED at Maturity 4 / Implementation 9 (fifteenth-wave completion train, 2026-07-03).** No longer consumer-assessed/N/A: it became an active in-repo category after ADR-027 shipped en-US + Spanish (superseding the single-locale ADR-011), and the ADR-027 Decision 9 train closed every stated hold: the pseudo-localization pass is a REQUIRED chromium CI gate (`PseudoLocalizationE2ETests`: `[!!` sentinel round-trip + overflow guard + `en-US` leak guard), the hard-coded-literal gate (`LocalizedTextConventionTests`) and the translation-coverage gate fail the build, and MudBlazor's built-in chrome localizes via `ResxMudLocalizer` (145 keys en+es). Held below 10 only by two locales / no RTL; on the protect list above.
- **#24 Forms/UX Safety: DONE for the shared surface (eighth wave, impl 7→8).** Register/Login are now `EditForm` + DataAnnotations + per-field `ValidationMessage` (typed models + `PasswordComplexity` attr + tests). Consumer module forms remain consumer-scored. **Maturity reached 4 on the fourteenth-wave re-score** (the CI-gated `AuthModelValidationTests` + `RegisterFormTests` meet the automatic-enforcement bar); the category is closed and on the protect list above.
- **#25 Navigation — DONE for the shared surface (eighth wave, impl 7→8):** an in-shell `Forbidden` (403) page + `NavigationFlow.md` for the Common UI surface. Per-actor module flows remain consumer-scored.

---

### Suggested sequencing
1. **MassTransit v8 fitness test** (#32 + #16) — one small test, closes two mediums, prevents a recurring prod crash.
2. **Broker retry policy** (#29 + #6) — the async path is the system's weakest seam.
3. **bUnit harness** (#28 + #18 + #19 guard) — unlocks the whole front-end tier.
4. **Erasure seam + outbox purge** (#30) — the only score-1 category; real compliance exposure.
5. Sweep the **fitness-function gaps** (#4, #11, #5) and **doc/CI hygiene** (#34, #17, #9, #13) as steady cleanup.
