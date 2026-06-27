# ADR-018: Polyglot Persistence (Multiple Storage Engines Behind One Model)

## Status
Accepted. The framework plumbing is complete, covered by unit and integration tests
(`DataSourceResolverTests`, `CrossDataSourceDegradeConventionTests`, `EntityTypeConfigurationTests`,
`CosmosConfigurationPortabilityTests`, `MultiSourceSqliteIntegrationTests`, and others), and shipped to
production (the engine-agnostic plumbing released in Common v1.79.0; see `FACTS.md` for the current
framework version). No production entity routes to a non-SQL-Server engine today. An end-to-end
trial (ADC's Conference `Session` to Cosmos DB and `Room` to SQLite, with its child entities) was built
and tested locally, then deliberately reverted to all-SQL-Server while every framework seam was kept.
Moving an aggregate to another engine later is a config-base-class change plus connection strings (and
one AppHost helper line), not a rewrite. This ADR records the decision and the seam, because the
machinery is load-bearing and already in production.

## Context
ADR-006 (database-per-service) splits storage along the **Name** axis: several physically separate
databases, all on the same engine (SQL Server), one per service. A second, orthogonal axis is the
storage **Engine** itself. Not every aggregate fits a relational store equally well:

- High-volume public read models (for example the conference's published-session surface) suit a
  document store with cheap horizontal reads.
- Transactional modules (Identity, Sales) want relational integrity and JOINs.
- Small, self-contained, or edge/offline data suits an embedded file database.

Putting every aggregate on SQL Server is a default, not a decision. We wanted the engine to be a
**per-aggregate choice driven by access pattern**, without that choice leaking into domain or
application code, and without rewriting an entity to move it between engines.

The `DataSourceKey(Engine, Name)` pair already present for ADR-006 carries an `Engine` component, so
routing by engine was a natural extension of the same resolver, registry, and context factory rather
than a separate subsystem.

## Decision
Support three storage engines behind one entity model and one set of repository abstractions, selected
per entity configuration.

1. **`DataSource` engine enum:** `SQLServer` (full relational JOINs), `CosmosDB` (document store, no
   cross-container JOINs), `Sqlite` (JOINs within one file). `DataSourceKey(Engine, Name)` identifies a
   physical source: the **Name** axis is ADR-006, the **Engine** axis is this ADR.
2. **Engine is a one-line declaration on the entity's configuration.** A configuration derives from an
   engine shim base (`EntityTypeConfigurationSQLServer` / `EntityTypeConfigurationCosmos` /
   `EntityTypeConfigurationSqlite`), or annotates `[UseDataSource(DataSource.X)]` directly. The
   engine-aware `EntityTypeConfiguration<TEntity, TId>` reads that attribute and applies the matching
   mapping (table + schema for SQL Server, table for SQLite, container + partition key for Cosmos) plus
   the right key generation (server identity, vs. client-side `CosmosIntIdValueGenerator`, vs. never).
   The configuration **body is portable**: moving an entity between engines is a single attribute
   change with no body edits.
3. **One concrete context per engine, one instance per database.** `SQLServerDbContext`,
   `SqliteDbContext`, and `CosmosDbContext` are sealed contexts over the abstract `ApplicationDbContext`.
   Combined with ADR-006's "one instance per `DataSourceKey`", a host materializes one context instance
   per physical (engine, name) source.
4. **Configuration drives routing.** `DataSourceResolver` builds a per-engine logical-to-physical map
   from the engine-specific connection strings (`SQLServerConnectionString` / `CosmosConnectionString` /
   `SqliteConnectionString`, plus `CosmosDatabaseName` and per-source `SQLServerMigrationsAssembly`).
   Logical names with no entry for an engine collapse onto that engine's `Default`; engines never
   collapse into each other. `EntityDataSourceRegistry` (and the `DataSourceService` facade) eagerly map
   every entity to its physical source up front, so routing never depends on a model already being built.
5. **Cross-engine relationships auto-degrade.** `CrossDataSourceDegradeConvention` removes FK constraints
   and navigations whose ends live in different physical sources (which now includes different engines);
   scalar FK columns plus a compensating index survive. Runtime joins flow through `INavigationPopulator`
   (ADR-002); cross-source consistency flows through the outbox (ADR-003).
6. **Cosmos specifics.** All of a module's entities share one container (so intra-module relationships
   and the navigation populators work), the entity Id is the partition key, Ids are generated client-side
   (`CosmosIntIdValueGenerator`, since a document store has no server identity), and relational-only
   constructs (indexes) are stripped at model-build time.

## Rationale
- **Right store per access pattern, as a configuration decision.** The engine becomes an attribute on a
  configuration class, not a rewrite. The same domain entity, application handler, and repository code
  run unchanged whichever engine backs the aggregate.
- **One mental model, one set of seams.** Polyglot persistence reuses the exact resolver, registry,
  context-factory, and degrade-convention machinery that database-per-service already needed, so there
  is no parallel data layer to maintain.
- **Portability lowers the cost of being wrong.** Because the configuration body is engine-agnostic, an
  aggregate that turns out to be a poor fit for its engine can be moved with an attribute change plus a
  data migration, not a code rewrite.

## Trade-offs
- **No cross-engine JOINs, FKs, or transactions.** This is the ADR-006 cost made sharper: across engines
  it is a hard limit, not a deployment choice. A query spanning engines (for example a public-session
  read that needs published-event Ids from a relational source) must be split into per-engine steps
  rather than one LINQ query, and consistency across engines is eventual via the outbox. The
  `CrossSourceSpecification` helper makes that split engine-portable (resolve principal keys, then filter
  by `FK IN (keys)` with no navigation), and the `SpecificationsDoNotNavigateToOtherEntities` fitness
  rule (the `specifications` category in ADR-015) fails the build if a specification silently embeds a
  cross-engine navigation.
- **Each engine carries its own operational model.** Separate EF provider, separate migration story
  (Cosmos has no relational migrations), separate backup/restore and cost profile. Adding an engine to a
  deployment is a real operational commitment, not just a connection string.
- **Cosmos constraints leak into modeling.** Container-per-module, Id-as-partition-key, and client-side
  Id generation are not the relational defaults; an aggregate moved to Cosmos must tolerate them.
- **Latent today.** The plumbing is complete, tested, and in production, but no production entity uses a
  non-SQL engine, so the cross-engine paths (degrade across engines, the `CrossSourceSpecification`
  helper, Cosmos client-side Id generation) are proven by tests and a reverted local trial rather than by
  production load until the first migration ships.

## Related
ADR-006 (database-per-service: the **Name** axis this ADR's **Engine** axis is orthogonal to; they share
`DataSourceKey`), ADR-002 (navigation populators bridge the relationships the degrade convention strips
across sources), ADR-003 (the outbox is the cross-source, and now cross-engine, consistency mechanism).
