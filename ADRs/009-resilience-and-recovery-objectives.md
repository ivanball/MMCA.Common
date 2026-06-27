# ADR-009: Resilience Policies & Recovery Objectives

## Status
Accepted (2026-06-14)

## Context
The framework already supplies the *mechanisms* for surviving partial failure — a standard Polly
resilience handler (timeout / retry / circuit breaker), the outbox for at-least-once delivery
(ADR-003), and database-per-service isolation (ADR-006). What was missing was a *stated contract*:

1. **No guaranteed coverage.** Resilience is applied per registration site (`AddTypedGrpcClient`,
   `AddTypedServiceClient`) plus a global `ConfigureHttpClientDefaults` default in `MMCA.Common.Aspire`.
   Nothing stopped a new outbound client from silently shipping with no retry/circuit-breaker.
2. **No recovery objectives.** Each consumer deploys its own databases, but RTO/RPO and the
   single-region-vs-failover decision were undocumented — "we'd figure it out" is not a plan, and an
   untested backup is not a backup (rubric §29).

## Decision
1. **Resilience is a framework invariant, not a per-call choice.** Every outbound `HttpClient` and
   gRPC client registered through the framework's extension methods (`AddTypedGrpcClient`,
   `AddTypedServiceClient`) wires the **standard resilience handler**, matching the global HTTP
   defaults in `MMCA.Common.Aspire`. This is enforced by a fitness function
   (`ResilienceHandlerTests` in `MMCA.Common.Grpc.Tests`) so the policy cannot silently regress.
2. **Consumers must declare recovery objectives.** Each consuming app documents, in its own
   `infra/DISASTER-RECOVERY.md`: RTO/RPO per failure scenario, the backup/restore mechanism, and an
   **explicit, signed-off** acceptance of single-region risk (or a multi-region failover plan).
   A restore must be *drilled* — the DR doc carries a drill-result table that cannot stay empty.
3. **Graceful degradation is the default posture.** When a synchronous dependency is unreachable,
   the resilience pipeline retries/breaks; cross-service consistency that can be deferred flows through
   the outbox (ADR-003), which buffers and guarantees eventual delivery after recovery.

### Reference objectives (MMCA.ADC — a regional, non-24×7 conference app)
| Scenario | RPO | RTO |
|---|---|---|
| Accidental data loss / bad migration (within retention) | ≤ ~10 min (continuous PITR) | ≤ 2 h |
| Single service DB corruption | ≤ ~10 min | ≤ 1 h (PITR restore-as-new, swap) |
| Full region loss | ≤ 1 h (geo-redundant backup) | ≤ 4 h (geo-restore + redeploy) |

ADC **deliberately accepts single-region risk**: sub-hour multi-region failover is not worth the
cost/complexity at its scale. A different consumer (e.g. a 24×7 store) is expected to set tighter
objectives and a failover plan in its own DR doc — the framework does not mandate one set of numbers,
only that the numbers exist and the restore is drilled.

## Rationale
- **Invariant over discipline.** A fitness function turns "remember to add resilience" into a build
  gate — the same approach the framework already uses for the layer rules and the MassTransit-v8 pin.
- **Objectives belong to the deployer.** RTO/RPO depend on the data and the business, which the
  framework can't know; it can only require that consumers decide and record them.
- **Drilled, not assumed.** The single most common DR failure is discovering at 2 a.m. that the
  backups never restored. Forcing a recorded drill closes the §29 gap that documentation alone leaves.

## Trade-offs
- The named gate (`ResilienceHandlerTests`, `MMCA.Common.Grpc.Tests`) asserts that the gRPC client path
  (`AddTypedGrpcClient`) *registers* the standard handler, not the runtime behavior of every policy
  parameter; parameter tuning is still a review concern. Runtime breaker behavior is no longer wholly
  untested, though: a separate fault-injection test (`ResilienceCircuitBreakerFaultInjectionTests`, same
  project) now drives sustained failures and proves the circuit breaker actually trips and short-circuits.
  `AddTypedServiceClient` wires the same standard handler but is not yet covered by an equivalent
  registration test.
- Per-consumer DR docs can drift from reality; the drill-result table is the mitigation (a stale table
  is a visible smell).
- A gRPC client that needs bespoke timeouts must override the standard handler explicitly rather than
  opt out of resilience entirely — intentional friction.
