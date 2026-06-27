# Resilience & Business Continuity (rubric §29)

MMCA.Common is a library, so it cannot *operate* a deployment — restores, RTO/RPO, and SLO alerting
are executed in the consumer apps' IaC (e.g. MMCA.ADC's `infra/DISASTER-RECOVERY.md`,
`scripts/dr-restore-drill.ps1`, `dr-drill.yml`, and the SLO metric-alerts in `infra/main.bicep`).
What the framework *can* do is (1) ship the failure-isolation and graceful-startup mechanisms, tested
centrally, and (2) give consumers a baseline SLO/error-budget template and a restore-drill runbook so
the recovery story is defined once and adopted, not reinvented per app. This complements
[`ADRs/009-resilience-and-recovery-objectives.md`](ADRs/009-resilience-and-recovery-objectives.md)
(the *decision*); this file is the *operational reference*.

## What the framework provides (and verifies in-repo)

| Mechanism | Where | Test |
|-----------|-------|------|
| Standard Polly resilience handler (timeout + retry w/ backoff + circuit breaker) on **every** outbound `HttpClient` and typed gRPC client | `Aspire/Extensions.cs` (`AddStandardResilienceHandler` via `ConfigureHttpClientDefaults`), `Grpc/DependencyInjection.cs`, `Infrastructure/DependencyInjection.cs` | `ResilienceCircuitBreakerFaultInjectionTests` (trips a breaker, asserts `BrokenCircuit` short-circuiting), `ResilienceHandlerTests` |
| Graceful degradation: integration-event publish failure buffers for redelivery (at-least-once) | `Infrastructure/Persistence/Outbox/OutboxProcessor.cs` | `OutboxProcessorTests.IntegrationEventPublishFailure_DegradesGracefully_BuffersForRedelivery` |
| Broker retry (exponential) on RabbitMQ / Azure Service Bus | `Infrastructure` (`ConfigureBrokerTransport`, `MessageBusSettings.RetryLimit/RetryMin/MaxIntervalSeconds`) | covered by messaging tests |
| Warm-up / readiness gate: holds `/health/ready` closed until startup warm-up runs, opens **even on task failure** (availability over warmth), pre-warms OIDC discovery to kill ACA cold-start | `Aspire/Warmup/` (`WarmupHostedService`, `WarmupReadinessGate`, `WarmupReadinessHealthCheck`, `OpenIdConnectMetadataWarmupTask`) — wired by `AddServiceDefaults` | `WarmupReadinessGateTests`, `WarmupReadinessHealthCheckTests`, `WarmupHostedServiceTests` (ADR-025) |

Failure isolation, graceful degradation, and graceful startup are therefore demonstrated and tested
centrally; the gaps a library structurally cannot fill — drilled restores, RTO/RPO numbers, and
measured SLOs — are the consumer's, with the templates below.

## Baseline SLO / error-budget template (consumers fill in)

Adopt and tune per app; ADC's filled-in version lives in `infra/DISASTER-RECOVERY.md` + the SLO
metric-alerts in `infra/main.bicep`.

| SLI | Suggested SLO | Error budget | Measured by |
|-----|---------------|--------------|-------------|
| Availability (successful requests / total) | 99.5% monthly | ~3.6 h/month | App Insights `requests` (failed count alert) |
| Latency (server response time) | p95 < 1 s (read), < 3 s (write) | — | App Insights `requests/duration` alert |
| Dependency success | 99.5% | — | App Insights `dependencies/failed` alert |
| Restore drill | ≥ 1 successful drill per release train, within the stated RTO | a missed/failed drill is a release-blocking §29 regression | the drill runbook below |

Define **RTO/RPO per service** (ADC's worked example):

| Scenario | RPO | RTO |
|----------|-----|-----|
| Accidental data loss / bad migration (within retention) | ≤ ~10 min (continuous PITR) | ≤ 2 h |
| Single-service DB corruption | ≤ ~10 min | ≤ 1 h |
| Full region loss | ≤ 1 h (geo-redundant backup lag) | ≤ 4 h |

A conscious, documented acceptance of single-region risk is a valid §29 posture — state it explicitly
(as ADC does) rather than leaving it implicit.

## Restore-drill runbook (reference)

The only evidence backups actually restore is a periodic **drill**: restore a throwaway copy, confirm
it comes back Online, record the measured restore time, then delete the copy. The live databases are
never touched. Run it **per release train** and after any backup/retention change.

```bash
# PITR restore of a throwaway COPY (Azure SQL example — adapt to your store)
az sql db restore -g <rg> -s <sqlServer> -n <Db> --dest-name <Db>-drill --time "<recent-utc>"
# verify status Online (and a row/table spot-check for a deeper check), record elapsed minutes, then:
az sql db delete -g <rg> -s <sqlServer> -n <Db>-drill --yes
```

Wire this as a manual/scheduled workflow + script (worked example: MMCA.ADC's
`.github/workflows/dr-drill.yml` + `scripts/dr-restore-drill.ps1`, which restore a copy, measure RTO,
verify Online, clean up, and emit a drill-result row). Record each run:

| Drill date | Source | Result |
|------------|--------|--------|
| _yyyy-mm-dd_ | _Db (PITR)_ | _PASS — restored in N min (RTO target …); status Online_ |
