# ADR-006: Database per Service

## Status
Accepted (2026-06-07). Supersedes the earlier "deliberately one shared database" stance.
Clarified 2026-06-27: the single context class became **one sealed context class per engine** when
ADR-018 added the orthogonal engine axis (the `Name`/database axis here is unchanged).

## Context
When the modules were first extracted into independently-deployable services, all services in an
app still pointed at a **single shared SQL database** with a single `OutboxMessages` table. That
left one significant defect: every service's `OutboxProcessor` polled the same outbox table with no
origin filter, so the services **raced** to claim each other's rows (ADR-003 / ArchitecturalAnalysis
§4.4 #5). It also undermined the data-autonomy half of "microservices" — services that share a
database are not independently evolvable at the schema level.

MMCA.Common already provided the machinery for multiple physical data sources (`DataSourceResolver`,
`EntityDataSourceRegistry`, `DbContextFactory`, `CrossDataSourceDegradeConvention`), so the move was
a configuration/deployment change, not a framework rewrite.

## Decision
Adopt **database-per-service**: each service owns its own physical database with its own
`OutboxMessages` table.

- **One sealed concrete context class *per engine*, one instance per database.** We do **not**
  introduce per-module DbContext classes (see the "Don't split SQLServerDbContext" convention). The
  default engine is SQL Server (`SQLServerDbContext`); ADR-018 later added `SqliteDbContext` and
  `CosmosDbContext`, each a sealed subclass of the abstract `ApplicationDbContext`. The right context
  class is materialized once per `DataSourceKey(Engine, Name)` by `PhysicalDbContextFactory`; entities
  route to a physical source by logical name (`[UseDatabase]` / module namespace) via
  `DataSourceResolver`, and to an engine by configuration base class (`[UseDataSource]`, ADR-018).
  The forbidden split is *per-module*, not per-engine.
- **ADC** runs `ADC_Identity`, `ADC_Conference`, `ADC_Engagement`, `ADC_Notification` — locally on
  the shared Aspire SQL container and in Azure as four Basic-tier databases. The legacy `AtlDevCon`
  database is retained **read-only** as an archive and rollback path.
- **Per-source outbox.** Each database has its own `OutboxMessages`; the `OutboxProcessor` drains
  only the sources its host owns, so no service ever sees another's rows.
- **Cross-service references are scalar IDs, not FKs.** `CrossDataSourceDegradeConvention` removes
  FK constraints/navigations that would span databases; runtime joins flow through
  `INavigationPopulator` (ADR-002), and cross-service consistency flows through the outbox + broker.
- A host with no `DataSources` configuration still collapses onto one `Default` source, so the
  single-database monolith deployment continues to work unchanged.

## Rationale
- **Removes the shared-outbox race** (the sharpest cost of the shared DB) without an `OriginService`
  filter — physical isolation is simpler and stronger than a logical filter.
- **Real data autonomy** — each service can evolve and scale its schema independently; lifts the
  former single-database scaling ceiling.
- **Reuses existing framework seams** — no new DbContext classes, no business-logic rewrite.

## Trade-offs
- **No cross-database FKs or transactions.** Relationships that span services degrade to scalar IDs;
  consistency across services is eventual (outbox + broker), not transactional.
- **More databases to provision, migrate, and back up.** Each service has its own migrations project
  and its own backup/restore concern.
- **Referential integrity across services is the application's responsibility** (compensating
  indexes survive, FK enforcement does not).
