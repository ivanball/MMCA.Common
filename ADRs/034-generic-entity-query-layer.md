# ADR-034: Generic Entity Controllers with a Dynamic Query Contract

## Status
Accepted (2026-06-30).

## Context
Every module exposes many entities, and most of them need the same read and write
surface: list, page, look up for a dropdown, fetch by id, create, delete. Hand
writing a controller, a query service, and a filter/sort/paginate implementation
per entity is repetitive, drifts in shape from one entity to the next, and is the
bulk of the boilerplate a modular monolith accumulates as it grows.

The framework chose the opposite default: a generic resource layer plus an
OData-lite dynamic query contract that every entity inherits for free, rather than
bespoke per-entity endpoints. A concrete controller is a few lines that close the
generic type parameters; the verbs, routes, filtering, sorting, pagination, field
projection, and include behavior come from the base. The mechanics are documented
in the onboarding chapters; the trade-off this represents (a generic, dynamically
queryable contract coupled to the entity model versus narrow bespoke endpoints) was
never recorded as a decision.

## Decision
Give every entity a generic REST resource surface and a bounded dynamic query
contract, supplied by two controller bases over a shared query pipeline.

1. **Generic read controller.** `EntityControllerBase<TEntity, TEntityDTO,
   TIdentifierType>`
   (`Source/Presentation/MMCA.Common.API/Controllers/EntityControllerBase.cs:28`,
   `[ApiController]` / `[Route("[controller]")]` / `[ApiVersion("1.0")]` at
   `EntityControllerBase.cs:25`) exposes four GET routes for any entity:
   `[HttpGet]` list (`EntityControllerBase.cs:73`), `[HttpGet("paged")]`
   (`EntityControllerBase.cs:112`), `[HttpGet("lookup")]` for id/name dropdown
   entries (`EntityControllerBase.cs:154`), and `[HttpGet("{id}")]`
   (`EntityControllerBase.cs:184`).

2. **Generic write controller.** `AggregateRootEntityControllerBase<TEntity,
   TEntityDTO, TIdentifierType, TCreateRequest>`
   (`Source/Presentation/MMCA.Common.API/Controllers/AggregateRootEntityControllerBase.cs:27`)
   inherits all of the above and adds `[HttpPost]` create
   (`AggregateRootEntityControllerBase.cs:58`, returning 201 `CreatedAtRoute` at
   `:72`) and `[HttpDelete("{id}")]`
   (`AggregateRootEntityControllerBase.cs:84`). The create action is decorated with
   `[Idempotent]` (`AggregateRootEntityControllerBase.cs:59`) so a retried POST does
   not create a duplicate (ADR-017).

3. **Sparse fieldsets via `fields`.** A comma-separated `fields` query parameter
   (`EntityControllerBase.cs:77`, `:121`, `:193`) drives a server-side projection:
   `QueryFieldService.ApplyFieldSelection`
   (`Source/Core/MMCA.Common.Application/Services/QueryFieldService.cs:140`) builds a
   `MemberInit` expression that selects only the requested writable properties so
   only those columns leave the database.

4. **Dynamic per-type filtering.** The paged route binds
   `Dictionary<string, (string Operator, string Value)> filters` through
   `[ModelBinder(typeof(QueryFilterModelBinder))]` (`EntityControllerBase.cs:124`),
   which parses `filters[Property].operator` / `filters[Property].value` query keys
   (`Source/Presentation/MMCA.Common.API/ModelBinders/QueryFilterModelBinder.cs:24`).
   `QueryFilterService.ApplyFilters`
   (`Source/Core/MMCA.Common.Application/Services/Filtering/QueryFilterService.cs:71`)
   resolves a `IFilterStrategy`
   (`Source/Core/MMCA.Common.Application/Services/Filtering/IFilterStrategy.cs:6`)
   per property CLR type from a strategy registry (string, bool, int, DateTime,
   decimal, Guid and their nullables, `QueryFilterService.cs:26`), each strategy
   declaring its `SupportedOperators` (`IFilterStrategy.cs:24`). Extra types register
   via `QueryFilterService.RegisterStrategy` (`QueryFilterService.cs:55`).

5. **Sort.** `sortColumn` / `sortDirection` (`EntityControllerBase.cs:119`)
   feed `QueryFieldService.ApplySorting` (`QueryFieldService.cs:120`), an
   `OrderBy("<col> ascending|descending")` over the entity property the DTO name
   maps to.

6. **Pagination and the `X-Pagination` header.** The paged route clamps the
   requested page size with `Math.Min(pageSize, MaxPageSize)`
   (`EntityControllerBase.cs:127`), where `MaxPageSize` reads
   `IApplicationSettings.MaxPageSize` and falls back to 500
   (`EntityControllerBase.cs:50`, default at
   `Source/Core/MMCA.Common.Application/Settings/ApplicationSettings.cs:15`). The
   pagination metadata is serialized into the `X-Pagination` response header
   (`EntityControllerBase.cs:144`).

7. **A last-resort safety ceiling.** Independent of the API page-size clamp,
   `EntityQueryPipeline.MaxUnboundedResultLimit = 1000`
   (`Source/Core/MMCA.Common.Application/Services/Query/EntityQueryPipeline.cs:22`)
   caps any unpaginated query with `query.Take(MaxUnboundedResultLimit)`
   (`EntityQueryPipeline.cs:100`, `:144`), so even a direct service caller that omits
   pagination cannot trigger an unbounded full-table load.

8. **Two include paths.** `includeFKs` / `includeChildren`
   (`EntityControllerBase.cs:69`) select navigation loading. `EntityQueryPipeline`
   (`EntityQueryPipeline.cs:13`) runs PATH 1 for source-supported includes via EF
   Core `.Include()` translated to SQL (`EntityQueryPipeline.cs:39`) and PATH 2 for
   unsupported includes via manual `INavigationPopulator` batch loading after
   materialization (`EntityQueryPipeline.cs:64`), the populator strategy of ADR-002.

## Rationale
- **Write the resource surface once, inherit it everywhere.** The verbs, routes,
  filter contract, pagination, and header are defined once on the two bases; a
  concrete controller closes the type parameters and gets a uniform, predictable
  contract. New entities cost almost nothing and cannot drift in shape.
- **Bounded dynamic querying, not open SQL.** Filtering is dynamic over the wire but
  not unbounded in the engine: each property is filtered only by a registered
  `IFilterStrategy` whose `SupportedOperators` are validated before the database is
  touched (`QueryFilterService.ValidateFilters`, `QueryFilterService.cs:124`,
  invoked at `Source/Core/MMCA.Common.Application/Services/EntityQueryService.cs:102`),
  and `MaxUnboundedResultLimit` (`EntityQueryPipeline.cs:22`) plus the `MaxPageSize`
  clamp (`EntityControllerBase.cs:127`) bound the result size.
- **Composes with manual DTO mapping (ADR-001).** Entities are projected to DTOs by
  an injected `IEntityDTOMapper` (`EntityQueryService.cs:32`, `:48`) via
  `DTOMapper.MapToDTOs` (`EntityQueryService.cs:137`); a `DTOToEntityPropertyMap`
  (`EntityQueryService.cs:58`) translates DTO field names to entity property paths
  for filter and sort, so the wire contract speaks DTO names while the engine speaks
  entity names.
- **Composes with populators (ADR-002).** The unsupported-include path delegates to
  `INavigationPopulator` (`EntityQueryService.cs:33`), the same cross-source batch
  loader that bridges relationships EF cannot JOIN.

## Trade-offs
- **The wire contract tracks the entity model.** Filterable, sortable, and
  projectable surface is the entity's property set. A model change is an API change
  unless mediated by the DTO and `DTOToEntityPropertyMap` (`EntityQueryService.cs:58`),
  which is the seam that decouples the two when needed.
- **Dynamic filtering is an injection and over-fetch surface.** Arbitrary
  client-supplied property/operator/value triples are an attack surface; it is
  bounded by validating properties and operators up front
  (`QueryFilterService.ValidateFilters`, `QueryFilterService.cs:124`), routing each
  type through its registered `IFilterStrategy` rather than free-form expression
  evaluation, and capping rows with `MaxUnboundedResultLimit`
  (`EntityQueryPipeline.cs:22`). Sparse fieldsets reject non-writable properties at
  projection (`QueryFieldService.cs:140`).
- **Generic endpoints are less self-documenting than bespoke ones.** One generic
  shape per entity is consistent but conveys less domain intent than a named,
  purpose-built endpoint; the query contract (filter key syntax, operators) must be
  learned once rather than read off each endpoint.
- **Opting out means overriding the base.** All four reads and the two writes are
  `virtual` (`EntityControllerBase.cs:76`, `AggregateRootEntityControllerBase.cs:63`),
  so a controller that needs bespoke behavior overrides the specific action rather
  than abandoning the base, but the default surface is opt-out, not opt-in.

## Related
ADR-001 (manual DTO mapping: the generic controllers project through
`IEntityDTOMapper`), ADR-002 (navigation populators: the unsupported-include path),
ADR-013 (Result pattern at the edge: every action returns through
`HandleFailure(result.Errors)`, `EntityControllerBase.cs:93`), ADR-017 (idempotency:
the generic create is `[Idempotent]`, `AggregateRootEntityControllerBase.cs:59`),
ADR-019 (rate limiting: these GET routes are the authenticated read surface the
always-on global limiter caps per principal).
