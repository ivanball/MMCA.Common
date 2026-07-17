# Cost & FinOps Notes (rubric §31)

MMCA.Common is a library, so it cannot *provision* anything — right-sizing, scale rules, budgets,
and per-service cost attribution live in the consumer apps' IaC (e.g. MMCA.ADC's `infra/main.bicep`,
`cost-guard.yml`, and budget alerts). What the framework *can* do is keep its own cost-relevant
defaults sane and document the levers consumers should set. This note consolidates the cost rationale
that previously lived only in code comments.

## What the framework does for cost

- **Telemetry ingestion is the real line item, so high-volume / low-value spans are dropped.**
  `OutboxPollFilterProcessor` (`MMCA.Common.Aspire`) suppresses the recurring `OutboxPoll` activity
  (and its `SqlClient` children) from OpenTelemetry export, so idle outbox polling does not dominate
  Log Analytics / App Insights ingestion. This is on by default in `AddServiceDefaults()`.
- **Head-based trace sampling is a built-in knob.** Set `Telemetry:TracesSampleRatio` to a value in
  `(0,1)` (e.g. `0.1` to keep 10% of traces) and `ConfigureOpenTelemetry` installs a
  `ParentBasedSampler(TraceIdRatioBasedSampler(ratio))` — ParentBased so a sampled-in request keeps
  its whole trace intact across services. Unset (the default) samples everything; an out-of-range or
  unparseable value also falls back to sample-everything, so a typo can never silently blind you. This
  is the single biggest lever on trace-ingestion cost once traffic grows.
- **High-volume metric families are drop-able knobs.** On a low-traffic multi-service deployment the
  `AppMetrics` table dominates ingestion, and two instrumentation families are ~85% of its data points:
  HttpClient connection/request metrics (`http.client.open_connections` / `active_requests` /
  `request.duration`: a high-frequency gauge stream from the pooled gRPC / service-discovery channels)
  and the .NET runtime metrics (`dotnet.gc.*` / `jit.*` / `thread_pool.*`: ~17 instruments emitted every
  collection interval regardless of traffic). Neither carries an end-user-visible signal. Set
  `Telemetry:DisableHttpClientMetrics=true` and/or `Telemetry:DisableRuntimeMetrics=true` and
  `ConfigureOpenTelemetry` skips that instrumentation; outbound-dependency latency is still captured as
  `AppDependencies` traces, and server-side RED metrics (`http.server.*` / `aspnetcore.*` / `kestrel.*`)
  are untouched. Unset (default) keeps them, so a host that does not opt in sees no change. Anything but
  a boolean `true` keeps the family, so a typo cannot silently blind it. On the MMCA apps this cut total
  Log Analytics ingestion ~70% (AppMetrics is ~80% of the workspace bill).
- **Per-message logs are kept off the `Information` channel.** The outbox's per-message
  "dispatched successfully" line is `Debug` (it is the single highest-volume log line in steady
  state); failures stay loud (dead-letter = `Error`, retry = `Warning`). So a busy outbox does not
  emit an `Information` line per message. Consumers should keep production at `Information` (or higher
  for noisy categories) rather than `Debug` — log volume is billed.
- **Idle compute is kept cheap.** The shared `SocketsHttpHandler` keep-alive / pooled-connection
  tuning in `MMCA.Common.Aspire` avoids per-request connection churn; on consumption-billed compute
  (e.g. Azure Container Apps) steady low traffic stays in the cheap idle band instead of repeatedly
  spinning vCPU.
- **The outbox poll interval is tunable and meant to be raised in production.** `OutboxProcessor`
  wakes on a signal (new rows written) and uses a smart wait, so real messages still flow in ~5s
  regardless of the fallback poll. `Outbox:PollingIntervalSeconds` therefore only controls *idle*
  polling — set it high in deployed environments (MMCA.ADC uses **300s** vs the 2s local default) to
  cut idle DB chatter and telemetry without adding message latency.
- **Outbox/inbox rows are purged, not kept forever.** `OutboxCleanupService` purges processed rows
  after `Outbox:RetentionDays` (default 7), bounding table growth (and the storage/scan cost of an
  ever-growing audit trail). Set `Outbox:RetentionDays = 0` to retain indefinitely if a consumer needs it.

## Recommended consumer defaults (set these downstream)

- **Telemetry retention & sampling.** Tune Log Analytics retention to the minimum the consumer's
  compliance window allows, and set `Telemetry:TracesSampleRatio` (the built-in head-based sampler
  knob above) on high-volume services. The framework emits at sensible levels; the
  *volume × retention* bill is a deployment choice.
- **Right-size from measured load, not worst case.** Size compute/database tiers to observed peak
  (MMCA.ADC sizes to its measured ~67-VU conference peak and runs Basic-tier SQL), and back scale
  rules with real traffic. A k6/load test that establishes the peak is cheaper than guessing high.
- **Make temporary scale-ups reversible.** Any conference-day / launch surge should have an automated
  or scheduled revert (MMCA.ADC's `cost-guard.yml` fails if a surge wasn't reverted to Basic tier).
- **Attribute spend.** Tag resources per service/environment so the bill is attributable, and add a
  budget + alert (MMCA.ADC sets a monthly RG budget with 80%/100% alerts).
- **Use the cheap tier for intermittent/archival workloads** (Basic-tier DBs, serverless/consumption
  compute, archived databases).

## Cost-attribution & guardrail samples (distilled from MMCA.ADC)

These belong in the consumer's IaC, not the library, but the framework documents the shape so every
consumer attributes spend and guards surges the same way. The worked, deployed versions are
`MMCA.ADC/infra/main.bicep` (tags + budget) and `MMCA.ADC/.github/workflows/cost-guard.yml`.

**Cost-attribution tags** — stamp every billable resource with a consistent tag set so Azure Cost
Analysis can group by application / environment / cost-centre:

```bicep
var commonTags = {
  application: 'myapp'          // groups the whole app's spend
  environment: environmentName  // prod / staging / dev
  component: 'my-component'
  managedBy: 'bicep'
  costCenter: 'my-cost-center'
}
// apply to every resource: resource x '...' = { ..., tags: commonTags }
```

Add a budget with alerts so the bill cannot surprise you:

```bicep
resource budget 'Microsoft.Consumption/budgets@2023-11-01' = if (enableBudget) {
  name: 'myapp-monthly'
  properties: {
    amount: monthlyBudgetAmount       // e.g. 200
    timeGrain: 'Monthly'
    notifications: { /* 80% actual + 100% forecasted → action group */ }
  }
}
```

**Surge-revert guard** — a scheduled, read-only check that fails if a temporary scale-up was left
running (the rubric's "scale-ups left running after the event" red flag). Distilled from
`cost-guard.yml`:

```yaml
# .github/workflows/cost-guard.yml — weekly + manual; READ-ONLY, never deploys.
on: { schedule: [{ cron: '0 7 * * 1' }], workflow_dispatch: {} }
jobs:
  drift:
    steps:
      - run: az login --service-principal --federated-token ... # OIDC, read-only role
      # Fail the run if any Container App maxReplicas > baseline (e.g. 2)
      # or any SQL DB tier != the agreed cheap tier (e.g. Basic), and print reset steps.
```

## Out of scope for the framework (by design)

Provisioning, scale rules, budgets, per-service cost attribution, and surge/revert automation are
consumer/IaC concerns and are *not* added to the library — see also `ADRs/009` (resilience/recovery
objectives are likewise the deployer's, not the framework's).
