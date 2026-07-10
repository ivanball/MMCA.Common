# ADR-001: Manual DTO Mapping over AutoMapper

## Status
Accepted. _Mechanism clarified 2026-06-26: the per-entity mappers are Riok.Mapperly source-generated (compile-time), not hand-written line by line. The decision to avoid runtime convention/reflection mapping (AutoMapper, Mapster) is unchanged._

## Context
Domain entities must be mapped to DTOs for API responses. The two common approaches are:
1. Manual mapping classes (`IEntityDTOMapper<TEntity, TDTO, TId>`)
2. Convention-based reflection mapping (AutoMapper, Mapster)

## Decision
Use explicit, per-entity DTO mappers (each a Riok.Mapperly `[Mapper] partial class` whose `MapToDTO` body is source-generated at compile time) registered via Scrutor assembly scanning, rather than a runtime convention/reflection mapper. The `IEntityDTOMapper<TEntity, TEntityDTO, TIdentifierType>` interface in MMCA.Common supplies the invariant `MapToDTOs` batch method as a **default interface method** (it just projects each item through `MapToDTO`); concrete mappers declare the `partial MapToDTO` that Mapperly generates (in practice each also re-declares the one-line `MapToDTOs` projection, a few add `[UserMapping]`/`[UseMapper]` helpers, and `SpeakerDTOMapper` hand-writes its public `MapToDTO` to wrap a private generated method so its PII-redaction rule can run). A parallel `IEntityRequestMapper<TEntity, TCreateRequest, TIdentifierType>` maps incoming create requests to entities via the entity factory, returning `Result<T>`.

## Rationale
- **Compile-time safety**: Mapping errors surface at build time, not runtime. Property renames break the build rather than silently mapping `null`.
- **Testability**: Each mapper is a small partial class whose generated `MapToDTO` is plain, steppable code with no runtime mapping framework, easily unit-tested in isolation.
- **Conditional logic**: Some mappers have business rules (e.g., `SpeakerDTOMapper` redacts PII for non-organizer roles). Convention-based tools make conditional mapping awkward.
- **Debuggability**: Stack traces point to a specific line in a specific mapper, not into a framework's pipeline.
- **Performance**: No reflection or expression compilation at mapping time.

## Trade-offs
- More files (28 DTO mappers across Store + ADC, plus the parallel `IEntityRequestMapper` classes). The interface's default `MapToDTOs` implementation is available to remove the batch-mapping boilerplate, though in practice each concrete mapper re-declares the identical one-line projection rather than relying on the default.
- Adding a new entity requires creating a mapper class. This is consistent with the project's explicit-over-implicit philosophy.
