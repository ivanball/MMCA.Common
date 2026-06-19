# ADR-005: Soft-Delete vs. Right-to-Erasure

## Status
Accepted

## Context
The framework's default deletion model is **soft-delete**: `AuditableBaseEntity.Delete()` sets `IsDeleted = true` and EF Core global query filters exclude the row from normal queries. The row — including any personal data it holds — stays in the database indefinitely, which is exactly what audit, referential integrity, and "undelete" (BR-135) require.

This conflicts with data-subject **erasure** rights (GDPR Art. 17 right-to-be-forgotten, CCPA deletion): when a person requests deletion, their personal data must actually be removed or anonymized, not merely hidden. Soft-delete alone is therefore non-compliant for personal data, and a consumer app's published privacy policy (e.g. a "we delete your data within 30 days" promise) cannot be honored by soft-delete.

A second source of retained personal data is the **outbox**: processed `OutboxMessage` rows hold serialized event payloads that may contain personal data and were previously never purged (ADR-003).

## Decision
Separate the two concerns and provide a seam for each, rather than overloading soft-delete:

1. **Soft-delete stays the default** for lifecycle/state management (hide + retain + undelete). It is explicitly *not* a privacy mechanism.
2. **Erasure is an explicit, additive capability.** Aggregates that store personal data implement `IAnonymizable` (`MMCA.Common.Domain.Interfaces`). An application-layer erasure handler loads the aggregate, calls `Anonymize()` (idempotent, returns `Result`), and saves — overwriting personal fields in place so foreign keys and the audit trail survive. Fields that must remain retrievable are persisted through the AES-256-GCM `EncryptedStringConverter`.
3. **Outbox retention is bounded.** `OutboxCleanupService` purges processed outbox rows older than `Outbox:RetentionDays` (default 7; set `0` to disable) across every relational data source, so event payloads are not retained indefinitely.

The framework provides the **seams** (`IAnonymizable`, `OutboxCleanupService`); each consumer app owns the **policy**: which entities are `IAnonymizable`, the erasure orchestration/endpoint (data-subject request handling), and any data-subject access/export endpoint — because the personal-data model lives in the consumer (e.g. ADC's `User`).

## Rationale
- **Right tool per concern**: soft-delete answers "is this record active?"; erasure answers "has this person's data been removed?". Conflating them (e.g. hard-deleting inside `Delete()`) would break audit, undelete, and referential integrity.
- **Audit-preserving**: anonymize-in-place keeps the row and its audit fields, satisfying both erasure and accountability obligations simultaneously.
- **Idempotent + Result-based**: matches the framework's domain conventions and tolerates retried erasure requests.
- **Bounded retention**: the cleanup service closes the "outbox grows forever / retains PII forever" gap noted in ADR-003 without changing delivery semantics.

## Trade-offs
- Erasure is opt-in per entity: an aggregate that holds personal data but does not implement `IAnonymizable` will not be erased — consumers must audit their personal-data inventory and implement the interface where needed.
- Anonymization is irreversible by design and is not the same operation as undelete.
- The framework cannot, on its own, make a consumer compliant: the consumer must still wire the erasure handler, the data-subject request flow, and access/export. This ADR provides the seams, not the policy.
- The default 7-day outbox retention is a **behavior change**: consumers upgrading the framework begin purging processed outbox rows older than 7 days unless they set `Outbox:RetentionDays = 0`.
