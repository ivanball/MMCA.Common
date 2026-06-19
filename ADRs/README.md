# Architecture Decision Records

Accepted ADRs explaining *why* the core cross-cutting patterns exist. Read these before changing a
pattern they describe — they capture context and trade-offs that aren't obvious from the code.

| # | Decision | Summary |
|---|----------|---------|
| [001](001-manual-dto-mapping.md) | Manual DTO mapping | Explicit mappers chosen over AutoMapper. |
| [002](002-navigation-populators.md) | Navigation populators | `INavigationPopulator<T>` for cross-container/cross-source eager loading. |
| [003](003-outbox-dual-dispatch.md) | Outbox dual dispatch | Outbox + in-process dispatch + background processor; at-least-once delivery. |
| [004](004-authentication-dual-fetch.md) | Authentication dual-fetch | JWKS discovery + fallback for cross-service token validation. |
| [005](005-soft-delete-vs-erasure.md) | Soft-delete vs. erasure | Soft-delete stays for lifecycle; `IAnonymizable` + outbox purge for GDPR/CCPA erasure. |
| [006](006-database-per-service.md) | Database per service | Each service owns its DB + outbox; one `SQLServerDbContext` class, one instance per DB. Removed the shared-outbox race (2026-06-07). |
| [007](007-grpc-extraction.md) | gRPC cross-service calls | `*.Contracts` + typed clients + `Result`-over-the-wire for synchronous inter-service calls. |
| [008](008-service-extraction-topology.md) | Monolith → services + Gateway | One service host per module (the monolith with one module enabled), fronted by a YARP Gateway; transport at the edge keeps it reversible. |
| [009](009-resilience-and-recovery-objectives.md) | Resilience & recovery objectives | Standard resilience handler on every outbound client (fitness-enforced); consumers must declare RTO/RPO + drilled restore + single-region acceptance. |
| [010](010-integration-event-schema-versioning.md) | Integration-event schema versioning | Every integration event carries a `SchemaVersion` (default 1, fitness-enforced); breaking changes use a new event type + upcaster, never a silent reshape. |
| [011](011-single-locale-i18n.md) | Single-locale by design (no i18n) | en-US only is a deliberate, revisitable non-goal (rubric §27); multi-locale would be greenfield work. |

## Writing a new ADR

Copy the structure of an existing record: **Status** (Proposed / Accepted / Superseded — date and
link when superseding), **Context** (the forces and the problem), **Decision** (what we chose, in
enough detail to implement), **Rationale** (why this over the alternatives), **Trade-offs** (what it
costs). Number sequentially (`NNN-kebab-title.md`) and add a row above. Keep ADRs short and
decision-focused; deep mechanics belong in `ArchitecturalAnalysis.md` or the per-project CLAUDE.md.
