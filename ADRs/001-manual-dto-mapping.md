# ADR-001: Manual DTO Mapping over AutoMapper

## Status
Accepted

## Context
Domain entities must be mapped to DTOs for API responses. The two common approaches are:
1. Manual mapping classes (`IEntityDTOMapper<TEntity, TDTO, TId>`)
2. Convention-based reflection mapping (AutoMapper, Mapster)

## Decision
Use explicit, hand-written DTO mappers registered via Scrutor assembly scanning. The `IEntityDTOMapper<TEntity, TEntityDTO, TIdentifierType>` interface in MMCA.Common supplies the invariant `MapToDTOs` batch method as a **default interface method** (it just projects each item through `MapToDTO`); concrete mappers implement `MapToDTO` only. A parallel `IEntityRequestMapper<TEntity, TCreateRequest, TIdentifierType>` maps incoming create requests to entities via the entity factory, returning `Result<T>`.

## Rationale
- **Compile-time safety**: Mapping errors surface at build time, not runtime. Property renames break the build rather than silently mapping `null`.
- **Testability**: Each mapper is a plain class with no framework magic, easily unit-tested in isolation.
- **Conditional logic**: Some mappers have business rules (e.g., `SpeakerDTOMapper` redacts PII for non-organizer roles). Convention-based tools make conditional mapping awkward.
- **Debuggability**: Stack traces point to a specific line in a specific mapper, not into a framework's pipeline.
- **Performance**: No reflection or expression compilation at mapping time.

## Trade-offs
- More files (27 DTO mappers across Store + ADC, plus the parallel `IEntityRequestMapper` classes). Mitigated by the interface's default `MapToDTOs` implementation, which eliminates the batch-mapping boilerplate.
- Adding a new entity requires creating a mapper class. This is consistent with the project's explicit-over-implicit philosophy.
