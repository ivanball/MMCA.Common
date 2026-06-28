# ADR-010: Integration-Event Schema Versioning & Upcaster Policy

## Status
Accepted (2026-06-19). Updated 2026-06-27 (Helpdesk enforcement gap closed; all three consumers now gate the convention).

## Context
Integration events cross service boundaries (Identity → Conference, Conference ↔ Engagement, …) and
are resolved by consumers solely by their type string: the outbox serializes the event to JSON keyed
by `EventType` (`OutboxMessage.FromDomainEvent`), and the MassTransit broker path binds by .NET message
type. Events carried only `MessageId` (idempotency) and `DateOccurred` (when the business action
happened) — **no version signal**. With database-per-service (ADR-006) and async integration over the
outbox (ADR-003), a producer that reshapes an event's payload can silently break every consumer: there
is nothing on the wire that says "this is a different shape than you expect," and no agreed rule for how
a shape may evolve. Rubric §6 flags this as the one substantive CQRS/event gap.

## Decision
1. **Every integration event carries an explicit `SchemaVersion`.** `BaseIntegrationEvent` exposes
   `public virtual int SchemaVersion => 1;`. It is serialized with the payload (System.Text.Json on the
   outbox path, MassTransit on the broker path), so a consumer always sees the producer's declared
   version. A fitness function asserts every concrete `IIntegrationEvent` declares an
   `int SchemaVersion`, so a new event cannot ship without one. The rule body
   (`ArchitectureRules.IntegrationEventsDeclareSchemaVersion`) lives once in the shared
   `MMCA.Common.Testing.Architecture` package and is surfaced through `EventConventionTestsBase`,
   alongside two companion rules: every integration event inherits `BaseIntegrationEvent` and resides
   in a Shared-layer `*.IntegrationEvents` namespace.
2. **Additive, optional changes keep the same version.** Adding a nullable/optional field, or a field
   with a safe default, is backward-compatible — consumers ignore unknown fields (System.Text.Json
   default) and old payloads deserialize with the default. No version bump required.
3. **Breaking changes require a new type + an upcaster, never a silent reshape.** Renaming, removing,
   or retyping a field is breaking. The producer introduces a NEW event type (e.g. `UserRegisteredV2`,
   `SchemaVersion => 2`) and publishes it; consumers register an **upcaster** that maps the old
   type/version to the new shape before their handler runs. The old type is retired only after all
   consumers have drained it (the broker binds by type, so a rename is a parallel-publish/drain
   migration, not an in-place edit).
4. **Rollout follows the framework's lockstep policy.** Adding `SchemaVersion` is a non-breaking
   `[C→A]` change (virtual default → existing events stay version 1 with no edits); it ships in a Common
   release and is swept into all consumers in one pass.

## Rationale
- **A signal, enforced.** A version field plus a build-gating convention test turns "remember the
  contract" into something the tooling checks — the same invariant-over-discipline approach as the layer
  rules, the MassTransit-v8 pin, and ADR-009's resilience gate.
- **Non-breaking by construction.** A `virtual` get-only default (`=> 1`) means no existing event
  changes and no outbox row migrates: System.Text.Json tolerates the missing field on old payloads and
  the type supplies the default; new rows simply gain `"schemaVersion":1`.
- **New-type-for-breaking-change** is the only safe option when transport binds by type (MassTransit)
  and consumers are independently deployed — an in-place reshape has no compatibility window.

## Trade-offs
- `SchemaVersion` is a **signal, not a mechanism**: by itself it does not stop a consumer breaking on a
  real reshape. The load-bearing half is the discipline (new type + upcaster); the framework does not
  yet ship an upcaster registration seam — building one is follow-up work, and until then the policy is
  enforced by convention + review, not by an upcaster pipeline.
- The convention test is **vacuous in MMCA.Common today** (the framework ships no concrete integration
  event): `EventVersioningConventionTests` runs the shared base against `CommonArchitectureMap` but has
  nothing to check. Real enforcement lives in the consumer repos: all three now subclass
  `EventConventionTestsBase` and run the identical rules against their own event assemblies:
  `EventConventionTests` in `MMCA.ADC.Architecture.Tests` (ADC's three events) and
  `MMCA.Store.Architecture.Tests` (Store's one), and a matching `EventConventionTests` in
  `MMCA.Helpdesk.Architecture.Tests` (`ArchitectureTests.cs`) that gates the seed's
  `TicketOpenedIntegrationEvent`. The earlier Helpdesk gap (the rule was once subclassed only in ADC and
  Store) is **closed**: every concrete integration event across all three consumers is now enforced.
- A get-only `SchemaVersion` is informational on the wire (it round-trips out, not back in) — intentional
  (version is a property of the type, not per-instance data), but it means you read it off the concrete
  type/JSON, not by mutating it.
