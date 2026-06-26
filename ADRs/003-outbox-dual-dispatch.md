# ADR-003: Outbox Pattern with Dual Dispatch

## Status
Accepted

## Context
Domain events must be reliably published after aggregate changes are persisted. Two failure modes exist:
1. In-process dispatch fails (e.g., handler throws) — the event is lost if not persisted.
2. Process crashes between persistence and dispatch — the event is lost if only dispatched in-memory.

## Decision
Use a dual-dispatch strategy:
1. **Outbox persistence**: Domain events are serialized into `OutboxMessage` rows within the same database transaction as the aggregate changes. This guarantees at-least-once persistence.
2. **In-process dispatch**: After `SaveChangesAsync`, events are dispatched immediately in-process via `DomainEventDispatcher` for low-latency handling.
3. **Background processor**: `OutboxProcessor` (a `BackgroundService`) wakes on an in-memory signal when new entries are written, or after a fallback polling interval (`Outbox:PollingIntervalSeconds`, default 2s; ADC prod sets 300s). Entries become eligible `Outbox:ProcessingDelaySeconds` after creation (default 5s); when a cycle sees pending-but-not-yet-eligible entries it **smart-waits** only until the earliest becomes eligible instead of sleeping the full interval. Eligible entries are retried up to 5 times before dead-lettering.

## Rationale
- **Guaranteed delivery**: The outbox table is written atomically with the aggregate changes. Even if the process crashes after persistence, the background processor catches up.
- **Low latency**: In-process dispatch handles the happy path without polling delay. In broker mode (`BrokerEventBus` persists the event to the outbox + signals; `OutboxProcessor` then publishes it to the broker via `IMessageBus`/`BrokerMessageBus`), the signal plus smart wait deliver integration events ~`ProcessingDelaySeconds` after publish even when the fallback interval is minutes long.
- **Idempotent handlers**: Domain event handlers must be idempotent since the same event may be dispatched both in-process and by the background processor if the in-process mark-as-processed fails.
- **Processing delay**: The eligibility delay prevents the background processor from re-dispatching events that were already dispatched in-process but not yet marked as processed. It bounds the duplicate-dispatch window — the in-process pipeline (save → dispatch → mark processed) must finish within it, or the event is re-dispatched (idempotency absorbs this).
- **Cheap idle polling**: A long fallback interval in deployed environments cuts idle DB chatter and its telemetry; additionally, the poll query runs inside an `OutboxPoll` activity that `OutboxPollFilterProcessor` (MMCA.Common.Aspire) suppresses from telemetry export, so idle polls do not flood Application Insights ingestion.

## Trade-offs
- Domain event handlers must be idempotent (this is a good practice regardless).
- The outbox table grows until processed entries are cleaned up — `OutboxCleanupService` purges rows whose `ProcessedOn` is older than `Outbox:RetentionDays` (default 7; set `0` to disable). See ADR-005.
- Dead-lettered messages (type not resolvable after 5 retries) require manual investigation.
- Failed-message retries pace at the polling interval: with a 300s prod interval, a persistently failing message dead-letters after ~25 minutes instead of seconds (an intentional, healthier backoff).
- Rows orphaned by a process crash (no signal exists) wait up to the polling interval before the safety-net pickup.
