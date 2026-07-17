# ADR-035: Optimistic Concurrency via RowVersion Round-Trip

## Status
Accepted (2026-07-02). Amended 2026-07-16: a child-entity overload of `SetOriginalRowVersion` was added (see Decision).

## Context
Every mutable aggregate in the framework is edited through a load-modify-save handler: the update use
case fetches the tracked entity, applies the request, and calls `SaveChangesAsync`. With one shared
context per engine (ADR-006) and no concurrency token, two editors who both read the same row and
save in turn silently overwrite each other. The second write wins, the first editor's change
vanishes, and nothing surfaces the collision. That last-write-wins default is fine for a single-user
admin tool and wrong for a multi-actor system where an organizer and a speaker can both be editing
the same session.

This is a **different concern** from the two idempotency mechanisms the framework already records.
ADR-017 (request idempotency) dedups a client retrying the **same** request, and ADR-021 (consumer
inbox) dedups the broker redelivering the **same** integration event. Both answer "I saw this one
action more than once." Optimistic concurrency answers the opposite question: "two **distinct**
actions targeted the same row, which one is stale?" We wanted a first-class, framework-wide mechanism
that turns a conflicting concurrent edit into a `409 Conflict` the caller can react to, rather than a
silent overwrite, and that a new mutable request cannot forget to opt into.

## Decision
Give every auditable entity a database-managed `RowVersion` concurrency token, round-trip it through
the client on updates, and stamp the client's last-seen value as EF's original value so a stale
update fails as a conflict.

- **A `RowVersion` token on the audit base.** `AuditableBaseEntity<TId>` carries a `byte[] RowVersion`
  property with a private setter, so every aggregate root and child entity inherits it. EF configures
  it on **every** non-owned `IAuditableEntity` in `ConfigureConcurrencyTokens`: SQL Server maps it to
  a server-generated `rowversion` (`IsRowVersion`), other relational providers map it as a plain
  application-managed token (`IsConcurrencyToken`). EF then includes the token in every UPDATE/DELETE
  `WHERE` clause and raises `DbUpdateConcurrencyException` when it matches no row.
- **`IConcurrencyAware` round-trips the token through the client.** Read DTOs expose the current
  `RowVersion` so a client can echo it back, and each `*UpdateRequest` implements `IConcurrencyAware`
  to carry the client's last-observed value. A null or empty `RowVersion` (creation, or a legacy
  client that never read one) skips the check by design.
- **`SetOriginalRowVersion` is the persistence seam.** `IWriteRepository.SetOriginalRowVersion`
  applies the client-supplied token as the tracked entity's **original** `RowVersion` value; the
  `EFRepository` implementation writes it to `Entry(entity).Property(nameof(RowVersion)).OriginalValue`
  and no-ops when the value is null or empty. The update handler calls it right after loading the
  entity (before applying the request), so EF compares the client's token against the row's current
  token inside the UPDATE statement, atomically, with no read-then-check race.
- **Child entities get the same protection through the `IRowVersioned` overload (2026-07-16
  amendment).** The original method is typed to the repository's aggregate root (`TEntity`), so a
  child edit (e.g. a `ProductVariant` under a `Product`) could not receive the token. A second
  overload, `SetOriginalRowVersion(IRowVersioned childEntity, byte[]? rowVersion)`, accepts any
  tracked auditable entity (`AuditableBaseEntity<TId>` implements the new
  `MMCA.Common.Domain.Interfaces.IRowVersioned`), stamping the child's original token with the same
  null-or-empty no-op contract. Update handlers that mutate children through the aggregate's
  repository call it per child after loading.
- **A conflict maps to `409 Conflict` at the edge.** `DbUpdateConcurrencyException` is a
  `DbUpdateException`, and `DbUpdateExceptionHandler` translates any `DbUpdateException` into a
  `409 Conflict` RFC 9457 ProblemDetails, with a generic detail message so the database schema is not
  leaked (the full exception is logged, not returned). This is the same edge that already returns 409
  for unique-constraint and foreign-key violations. (The parallel `Result`-based path,
  `ErrorType.Conflict`, maps to 409 through `ErrorHttpMapping` for handlers that model a conflict as a
  `Result` rather than let the exception propagate.)
- **A fitness function enforces the opt-in build-wide.** `ArchitectureRules.UpdateRequestsAreConcurrencyAware`
  scans module Application assemblies for types whose simple name ends in `UpdateRequest` and flags any
  that do not implement `IConcurrencyAware`. `ConcurrencyConventionTestsBase` exposes it as a single
  `[Fact]`, and **both** consumers subclass it (`MMCA.ADC.Architecture.Tests` and
  `MMCA.Store.Architecture.Tests` each supply their own `IArchitectureMap`). A module with no mutable
  aggregate is legitimately vacuous.
- **Adopted per database via an `AddRowVersionToAllEntities` migration.** Each consumer added the
  `RowVersion` column to every table in one migration (`AddRowVersionToAllEntities` in both ADC and
  Store), typed `rowversion` with an empty-byte default, so existing rows get a token on first write.

## Rationale
- **Database-managed token over a hand-maintained version field.** A SQL Server `rowversion`
  auto-increments on the server on every write; no domain code sets or reads it (the setter is
  private, populated by EF). The token stays invisible to the aggregate's behavior, so concurrency is
  a persistence concern, not a domain one.
- **Round-trip over a server-side reload.** Reloading and re-saving on the server hides the collision
  (both writes succeed against the freshest row). Forcing the client to echo the token it last read
  is what makes a competing edit a real conflict instead of a silent overwrite.
- **One seam over a per-handler compare.** `SetOriginalRowVersion` plus EF's `WHERE`-clause check does
  the comparison in the database, atomically with the UPDATE. A hand-rolled "read the current token,
  compare, then save" would reintroduce the exact race it is meant to close.
- **Invariant over discipline (ADR-015).** The `*UpdateRequest` naming convention is enforced
  mechanically in both consumers, so a newly added mutable request cannot ship without the token by an
  author simply forgetting. This is the same posture the framework prefers elsewhere.
- **Reuse the existing 409 edge.** `DbUpdateExceptionHandler` already funnels every `DbUpdateException`
  to 409, so concurrency conflicts inherit the same translation, logging, and schema-safe message as
  constraint violations with no new middleware.

## Trade-offs
- **Opt-in at the caller, not just the type.** A null or empty `RowVersion` skips the check, so a
  client that never echoes the token still gets last-write-wins. The fitness function guarantees the
  request **type** carries the field; it cannot guarantee a given caller populates it.
- **The 409 is coarse.** `DbUpdateExceptionHandler` maps all `DbUpdateException`s to one 409 with a
  generic message, so from the status and body alone a client cannot tell a concurrency conflict from
  a unique-constraint or foreign-key violation. That is deliberate (no schema leak), but it means
  retry logic treats the three the same.
- **Cross-engine asymmetry.** SQL Server gets a server-generated `rowversion`; SQLite and other
  relational providers get an application-managed `IsConcurrencyToken` over the same `byte[]` (EF
  sends the value on INSERT rather than expecting the database to generate it). Cosmos has its own
  ETag concurrency mechanism that is not routed through this property.
- **Enforcement is bound to a naming convention.** The rule keys on the `UpdateRequest` suffix. A
  mutable request that does not follow that suffix is outside the rule's scope.
- **Adoption is a per-database migration.** `AddRowVersionToAllEntities` adds the column to every
  existing table; a new database, or a table added later, must carry the column for the token to
  exist there.

## Related
ADR-017 (HTTP request idempotency, which dedups retries of the **same** request, the mirror-image
concern to two **distinct** edits racing here), ADR-021 (consumer-side inbox, which dedups broker
redeliveries of the **same** event, likewise distinct from concurrency), ADR-006 (the one-shared-
context-per-engine model over which the `RowVersion` token is configured uniformly), ADR-015 (the
fitness-function-over-discipline enforcement this reuses), ADR-005 (soft-delete and audit fields live
on the same `AuditableBaseEntity` base that carries `RowVersion`).
