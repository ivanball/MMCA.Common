# ADR-003: Outbox Pattern with Dual Dispatch

## Status
Accepted. Revised 2026-07-19 (integration-event routing via `IMessageBus`, lease-based claims for
safe scale-out, dead-letter visibility, post-commit dispatch; see Revision below).

## Context
Domain events must be reliably published after aggregate changes are persisted. Two failure modes exist:
1. In-process dispatch fails (e.g., handler throws) — the event is lost if not persisted.
2. Process crashes between persistence and dispatch — the event is lost if only dispatched in-memory.

## Decision
Use a dual-dispatch strategy:
1. **Outbox persistence**: Domain events are serialized into `OutboxMessage` rows within the same database transaction as the aggregate changes. This guarantees at-least-once persistence.
2. **In-process dispatch**: After `SaveChangesAsync`, events are dispatched immediately in-process via `DomainEventDispatcher` for low-latency handling.
3. **Background processor**: `OutboxProcessor` (a `BackgroundService`) wakes on an in-memory signal when new entries are written, or after a fallback polling interval (`Outbox:PollingIntervalSeconds`, default 2s; ADC prod sets 300s). Entries become eligible `Outbox:ProcessingDelaySeconds` after creation (default 5s); when a cycle sees pending-but-not-yet-eligible entries it **smart-waits** only until the earliest becomes eligible instead of sleeping the full interval. Eligible entries that throw during dispatch are retried up to 5 times, then dropped from the eligible set (a message whose event type cannot be resolved is dead-lettered immediately on first pickup; see Trade-offs).

## Rationale
- **Guaranteed delivery**: The outbox table is written atomically with the aggregate changes. Even if the process crashes after persistence, the background processor catches up.
- **Low latency**: In-process dispatch handles the happy path without polling delay. In broker mode (`BrokerEventBus` persists the event to the outbox + signals; `OutboxProcessor` then publishes it to the broker via `IMessageBus`/`BrokerMessageBus`), the signal plus smart wait deliver integration events ~`ProcessingDelaySeconds` after publish even when the fallback interval is minutes long.
- **Idempotent handlers**: Domain event handlers must be idempotent since the same event may be dispatched both in-process and by the background processor if the in-process mark-as-processed fails.
- **Processing delay**: The eligibility delay prevents the background processor from re-dispatching events that were already dispatched in-process but not yet marked as processed. It bounds the duplicate-dispatch window — the in-process pipeline (save → dispatch → mark processed) must finish within it, or the event is re-dispatched (idempotency absorbs this).
- **Cheap idle polling**: A long fallback interval in deployed environments cuts idle DB chatter and its telemetry; additionally, the poll query runs inside an `OutboxPoll` activity that `OutboxPollFilterProcessor` (MMCA.Common.Aspire) suppresses from telemetry export, so idle polls do not flood Application Insights ingestion.

## Trade-offs
- Domain event handlers must be idempotent (this is a good practice regardless).
- The outbox table grows until processed entries are cleaned up — `OutboxCleanupService` purges rows whose `ProcessedOn` is older than `Outbox:RetentionDays` (default 7; set `0` to disable). See ADR-005.
- Two distinct failure mechanisms exist. A message whose event **type cannot be resolved** is
  dead-lettered immediately on first pickup (it can never succeed) and requires manual investigation.
  A message that **throws during dispatch** is retried up to `Outbox:MaxRetries` (default 5) times,
  then dropped from the eligible set (it stops being polled once `RetryCount >= MaxRetries`).
- Failed-message retries pace at the polling interval: with a 300s prod interval, a persistently failing message dead-letters after ~25 minutes instead of seconds (an intentional, healthier backoff).
- Rows orphaned by a process crash (no signal exists) wait up to the polling interval before the safety-net pickup.

## Revision (2026-07-19)
Four changes from the 2026-07-19 full review:

1. **Integration events route through the outbox to `IMessageBus`, never local dispatch.** An
   `IIntegrationEvent` raised via `AddDomainEvent` used to be dispatched in-process and marked
   processed, silently never reaching the wire in broker mode. Now
   `DomainEventSaveChangesInterceptor` writes its outbox row but does NOT dispatch it in-process;
   the row stays unprocessed and `OutboxProcessor` publishes it via `IMessageBus`, so the
   registered transport (in-process for the monolith, MassTransit broker for extracted services)
   determines delivery. `AddDomainEvent(integrationEvent)` is therefore broker-correct. Pure
   domain events keep the dual-dispatch fast path described above.
2. **Lease-based claims make scale-out safe by construction.** `OutboxMessage` gains `LockedUntil`
   and `LockToken`: before dispatching, a processor replica claims the eligible batch with an
   atomic `ExecuteUpdateAsync` lease (`Outbox:LeaseSeconds`, default 300); other replicas skip
   rows under an unexpired lease and a race between two claim updates resolves per row (each
   replica processes only rows carrying its own token). A replica that dies mid-batch releases its
   rows implicitly when the lease expires. Running `minReplicas: 1` is therefore **no longer a
   correctness requirement for the outbox** (previously two replicas could drain the same rows and
   double-dispatch every event); it remains a cost choice, and ADR-030's sole-migrator rationale
   for the setting stands on its own.
3. **Dead-letter visibility.** Retry exhaustion is now loud: the `outbox.dead_letter.count` metric
   gets a `reason=retries_exhausted` tag (beside the existing `type_unresolvable`), an Error-level
   log fires at the moment of exhaustion (the operator's last signal before the row leaves the
   poll), and `Outbox:DeadLetterRetentionDays` retains dead-lettered payloads longer than
   `Outbox:RetentionDays` (0 = same retention) for diagnosis and manual replay before
   `OutboxCleanupService` purges them.
4. **In-process dispatch defers until after commit.** When the save runs inside a transaction (the
   ADR-014 Transactional path), the post-save dispatch/mark-processed work is deferred and flushed
   only after a successful commit; rollback (exception or the new business-failure rollback,
   ADR-014 Revision) drops it together with the outbox rows.
