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
  compliance window allows, and apply OpenTelemetry sampling on high-volume traces. The framework
  emits at sensible levels; the *volume × retention* bill is a deployment choice.
- **Right-size from measured load, not worst case.** Size compute/database tiers to observed peak
  (MMCA.ADC sizes to its measured ~67-VU conference peak and runs Basic-tier SQL), and back scale
  rules with real traffic. A k6/load test that establishes the peak is cheaper than guessing high.
- **Make temporary scale-ups reversible.** Any conference-day / launch surge should have an automated
  or scheduled revert (MMCA.ADC's `cost-guard.yml` fails if a surge wasn't reverted to Basic tier).
- **Attribute spend.** Tag resources per service/environment so the bill is attributable, and add a
  budget + alert (MMCA.ADC sets a monthly RG budget with 80%/100% alerts).
- **Use the cheap tier for intermittent/archival workloads** (Basic-tier DBs, serverless/consumption
  compute, archived databases).

## Out of scope for the framework (by design)

Provisioning, scale rules, budgets, per-service cost attribution, and surge/revert automation are
consumer/IaC concerns and are *not* added to the library — see also `ADRs/009` (resilience/recovery
objectives are likewise the deployer's, not the framework's).
