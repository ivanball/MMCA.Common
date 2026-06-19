# ADR-001: Manual DTO Mapping over AutoMapper

## Status
Accepted

## Context
Domain entities must be mapped to DTOs for API responses. The two common approaches are:
1. Manual mapping classes (`IEntityDTOMapper<TEntity, TDTO, TId>`)
2. Convention-based reflection mapping (AutoMapper, Mapster)

## Decision
Use explicit, hand-written DTO mappers registered via Scrutor assembly scanning. A shared `EntityDTOMapperBase<TEntity, TDTO, TId>` abstract class in MMCA.Common provides the invariant `MapToDTOs` batch method; concrete mappers override `MapToDTO` only.

## Rationale
- **Compile-time safety**: Mapping errors surface at build time, not runtime. Property renames break the build rather than silently mapping `null`.
- **Testability**: Each mapper is a plain class with no framework magic, easily unit-tested in isolation.
- **Conditional logic**: Some mappers have business rules (e.g., `SpeakerDTOMapper` redacts PII for non-organizer roles). Convention-based tools make conditional mapping awkward.
- **Debuggability**: Stack traces point to a specific line in a specific mapper, not into a framework's pipeline.
- **Performance**: No reflection or expression compilation at mapping time.

## Trade-offs
- More files (27 mappers across Store + ADC). Mitigated by `EntityDTOMapperBase` which eliminates the `MapToDTOs` boilerplate.
- Adding a new entity requires creating a mapper class. This is consistent with the project's explicit-over-implicit philosophy.
