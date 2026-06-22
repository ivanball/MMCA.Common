# ADR-002: NavigationPopulators for Cross-Container Loading

## Status
Accepted

## Context
The application supports multiple database backends (SQL Server, Cosmos DB, SQLite). EF Core's `.Include()` works for SQL Server but fails for Cosmos DB cross-container relationships and certain SQLite configurations. Navigation properties are tagged with `[Navigation]` (the attribute's `IsCollection` flag distinguishes a child collection from an FK reference). Whether a given navigation can actually be loaded via `Include` is *not* a static attribute value: it is classified at runtime by `NavigationMetadataProvider`, which asks `IDataSourceService.HaveIncludeSupport(declaringType, targetType)`. Include is used when both ends share one relational source; a navigation whose ends live in different containers or databases (cross-container Cosmos, cross-database under database-per-service) is classified **unsupported** and routed to manual population instead.

## Decision
Each entity that has unsupported navigations gets a `INavigationPopulator<TEntity>` implementation. A `DeclarativeNavigationPopulator<TEntity>` base class (added in MMCA.Common) allows populators to be defined as a list of `INavigationDescriptor<TEntity>` declarations rather than imperative if-check boilerplate.

Two descriptor types exist:
- `ChildNavigationDescriptor` — for child collection navigations (e.g., `Event.Rooms`)
- `FKNavigationDescriptor` — for FK reference navigations (e.g., `Product.Category`)

Both delegate to `NavigationLoader`, which batch-loads related entities in a single `WHERE FK IN (...)` query to avoid N+1 problems.

## Rationale
- **Multi-DB support**: The query pipeline automatically falls back from Include to NavigationPopulator when the data source reports navigations as unsupported.
- **Batch efficiency**: All related entities for all parents are loaded in one query per navigation, not per-parent.
- **Declarative**: The `DeclarativeNavigationPopulator` eliminates ~30-40 lines of repeated if-checks per entity. Each populator is now just a list of descriptors.

## Trade-offs
- Extra abstraction layer for SQL Server (where Include works fine). Mitigated: the populator is only called when the query pipeline's metadata says navigations are unsupported.
- Entities that need no manual loading still require a no-op populator (or use `NullNavigationPopulator`).
