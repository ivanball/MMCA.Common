# ADR-021: Consumer-Side Inbox for Integration-Event Idempotency

## Status
Accepted (2026-06-09).

## Context
ADR-003 makes integration-event delivery **at-least-once**: the outbox guarantees a published event
is not lost, and the MassTransit broker redelivers on consumer failure. At-least-once means a
consumer can legitimately see the **same event more than once** (broker redelivery after a transient
handler failure, a redeploy mid-consume, or a lost ack). ADR-003's answer was "domain-event handlers
must be idempotent," but that pushes the dedup burden onto every handler author and is easy to get
wrong. We wanted a single, reusable place that recognizes an already-processed integration event and
skips it, without changing how handlers are written.

This is the broker-consumer sibling of two idempotency concerns the framework already records:
ADR-003 (the producer/outbox side plus handler idempotency) and ADR-017 (the inbound HTTP edge,
deduping client retries). The inbox is the third leg: deduping **broker redeliveries** at the
consume edge.

## Decision
Add an opt-in **inbox** that records each successfully-processed integration event by its `MessageId`
and skips redeliveries.

- **Every event carries a `MessageId`.** `BaseDomainEvent` stamps a unique `MessageId`; it is the
  dedup key (the same id the outbox serializes and the broker carries).
- **`IInboxStore` with two implementations.** `EfInboxStore` (active) records processed messages in
  an `InboxMessages` table; `NoOpInboxStore` (default) never dedups. The switch is the
  `MessageBus:EnableInbox` flag (default `false`): `AddBrokerMessaging` registers `EfInboxStore` when
  set and `NoOpInboxStore` otherwise.
- **Check before, record after.** The generic `IntegrationEventConsumer<TEvent>` calls
  `AlreadyProcessedAsync(MessageId)` first and skips the handlers (acking the message) when it is a
  duplicate; it calls `MarkProcessedAsync(MessageId, eventType)` only **after** all handlers succeed.
  A handler that throws rethrows, so MassTransit applies its retry/dead-letter policy and the message
  stays un-recorded (eligible for redelivery).
- **The inbox lives in the consumer's own database.** Rows are written to the host's outbox data
  source (`Outbox:DataSource` / `Outbox:DatabaseName`), so each service dedups in its own database,
  consistent with database-per-service (ADR-006). Every relational source gets an `InboxMessages`
  table; Cosmos hosts skip it.
- **A unique index is the concurrency guard.** `IX_InboxMessages_MessageId` is unique; a concurrent
  duplicate that races past the `AlreadyProcessedAsync` check fails its insert with `DbUpdateException`,
  which `EfInboxStore` swallows as "already processed."
- **Bounded retention.** `OutboxCleanupService` purges inbox rows older than `Outbox:RetentionDays`
  alongside outbox rows (gated on `EnableInbox`), so the table does not grow forever (ADR-005).

The delivery guarantee is therefore **at-least-once-with-dedup**, not exactly-once: a crash between a
handler's commit and the inbox write reprocesses the event exactly once more, so **handlers must
still be idempotent** for that narrow window. The inbox removes the routine-duplicate burden; it does
not make handlers free to be non-idempotent.

In production the inbox is enabled on the services that consume integration events: ADC's Identity
and Conference services and Store's Sales service set `EnableInbox: true`, each with an
`AddInboxMessages` migration on its own database.

## Rationale
- **Dedup once, not in every handler.** A single consume-edge check turns "every handler author must
  remember to be idempotent against redelivery" into a framework guarantee for the common case, the
  same invariant-over-discipline posture the framework prefers (ADR-015).
- **Record-after-success is the correct ordering.** Marking processed only after handlers succeed
  means a failure leaves the message redeliverable; marking before would risk dropping an event whose
  handler then failed.
- **Physical isolation reuses database-per-service.** Putting the inbox in the consumer's own outbox
  database needs no new infrastructure and keeps each service self-contained, with no shared dedup
  store to race on.
- **Opt-in keeps the monolith simple.** In-process dispatch (ADR-003) never redelivers, so a
  single-process or broker-less deployment needs no inbox; `NoOpInboxStore` is the default and costs
  nothing.

## Trade-offs
- **Not exactly-once.** The crash-after-handler-before-inbox window reprocesses once, so handlers must
  stay idempotent for it; the inbox narrows the duplicate window, it does not close it.
- **Opt-in per service.** A broker-consuming service that forgets `EnableInbox` gets no dedup (and no
  `InboxMessages` table), the same audit-the-inventory caveat as ADR-005 / ADR-017 / ADR-020.
  Enabling it also requires the migration that creates the table.
- **A second housekeeping table.** Each consumer database carries an `InboxMessages` table and its
  retention purge, in addition to the outbox.
- **Dedup is keyed on `MessageId`, not payload.** Dedup is per-message-identity (the intended
  granularity); a producer that re-published the *same* business action under a *new* `MessageId`
  would not be deduped by the inbox.

## Related
ADR-003 (the outbox and at-least-once delivery whose consumer side this deduplicates; handler
idempotency is still required for the crash window), ADR-006 (the inbox lives in the consumer's own
database), ADR-005 (`OutboxCleanupService` bounds inbox retention too), ADR-017 (the inbound-HTTP-edge
idempotency this mirrors at the broker-consume edge).
