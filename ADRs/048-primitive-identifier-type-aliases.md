# ADR-048: Primitive Identifier Type Aliases over Strongly-Typed ID Structs

## Status
Accepted (2026-07-15).

## Context
Every entity needs an identity type. The framework's base entity is generic over that type:
`BaseEntity<TIdentifierType>` constrains it to `notnull` and exposes a single `required init Id`
(`Source/Core/MMCA.Common.Domain/Entities/BaseEntity.cs:14-17`), over the equally generic
`IBaseEntity<TIdentifierType>` (`Source/Core/MMCA.Common.Domain/Interfaces/IBaseEntity.cs:7-11`). That
generic parameter accepts either of the two common identity styles:

1. A **primitive** (`int`, `Guid`) named through a per-entity alias (`UserIdentifierType = int`).
2. A **strongly-typed wrapper struct** (`readonly record struct UserId(int Value)`) as tactical DDD
   prescribes, to make identifiers non-interchangeable at compile time.

The codebase chose the first, but until now that choice lived only as a CLAUDE.md convention with no
recorded trade-off. The wrapper-struct alternative was scaffolded, not written: a `StronglyTypedIds`
folder exists in `MMCA.Common.Shared` and in every module's `.Shared` project (for example
`MMCA.ADC/Source/Modules/Conference/MMCA.ADC.Conference.Shared/StronglyTypedIds`,
`MMCA.Store/Source/Modules/Catalog/MMCA.Store.Catalog.Shared/StronglyTypedIds`) and each contains **no
types**. This ADR records the primitive-alias decision and its cost so the deferral is deliberate and
legible.

## Decision
Model every identifier as a **primitive named through a global-using alias**, declared per module,
not as a wrapper struct.

- **Identity is a primitive behind an alias.** Each module declares
  `global using {Entity}IdentifierType = <primitive>;` in its `.Shared` project. Common's own
  aggregate root uses `UserIdentifierType = int`
  (`Source/Core/MMCA.Common.Domain/GlobalUsings.IdentifierType.cs:1`), with the push-notification
  aliases alongside it (`Source/Core/MMCA.Common.Shared/GlobalUsings.NotificationIdentifierType.cs:1-2`).
  Consumers follow the same pattern: ADC Identity
  (`MMCA.ADC/Source/Modules/Identity/MMCA.ADC.Identity.Shared/MMCA.ADC.Identity.GlobalUsings.IdentifierType.cs:2`),
  ADC Conference with fifteen aliases
  (`MMCA.ADC/Source/Modules/Conference/MMCA.ADC.Conference.Shared/MMCA.ADC.Conference.GlobalUsings.IdentifierType.cs:5-19`),
  and Store Catalog
  (`MMCA.Store/Source/Modules/Catalog/MMCA.Store.Catalog.Shared/MMCA.Store.Catalog.GlobalUsings.IdentifierType.cs:3-6`).
- **The alias is the type; there is no wrapping struct.** The right-hand side is a bare primitive.
  Most resolve to `int`; the one deviation in Conference is
  `SpeakerIdentifierType = System.Guid` (line 18), because Sessionize assigns speakers GUIDs while its
  other imported entities carry integer IDs (the file header comment records this,
  `MMCA.ADC.Conference.GlobalUsings.IdentifierType.cs:1-4`).
- **Aliases are linked solution-wide via `Directory.Build.props`.** Each `GlobalUsings.*.cs` file is
  pulled into every project with a `<Compile Include ... Link=... />` block, so the alias is visible
  everywhere without a project reference: Common
  (`MMCA.Common/Directory.Build.props:76-77,82-83`), ADC
  (`MMCA.ADC/Directory.Build.props:66-76`), Store (`MMCA.Store/Directory.Build.props:66-73`). Adding a
  solution-wide alias is a new `GlobalUsings.*.cs` plus a matching `<Compile Include>` line, nothing more.
- **The alias flows unchanged through every layer.** Tracing the ADC `User` aggregate: the domain
  entity is `User : AuditableAggregateRootEntity<UserIdentifierType>`
  (`MMCA.ADC/Source/Modules/Identity/MMCA.ADC.Identity.Domain/Users/User.cs:17`); the cross-context
  reference to a speaker is typed `SpeakerIdentifierType? LinkedSpeakerId` (same file, line 53); the EF
  configuration is `EntityTypeConfigurationSQLServer<User, UserIdentifierType>`
  (`MMCA.ADC/Source/Modules/Identity/MMCA.ADC.Identity.Infrastructure/Persistence/EntityConfiguration/UserConfiguration.cs:13`);
  the repository handle is `GetRepository<User, UserIdentifierType>()`
  (`MMCA.ADC/Source/Modules/Identity/MMCA.ADC.Identity.Infrastructure/Persistence/DbContexts/Seeding/IdentityModuleDbSeeder.cs:34`);
  the API contract is `UserDTO : IBaseDTO<UserIdentifierType>` with a `UserIdentifierType Id`
  (`MMCA.ADC/Source/Modules/Identity/MMCA.ADC.Identity.Shared/Users/UserDTO.cs:8,11`); and the
  integration event carries `UserIdentifierType UserId`
  (`MMCA.ADC/Source/Modules/Identity/MMCA.ADC.Identity.Shared/Users/IntegrationEvents/UserRegistered.cs:24`).
  No converter, serializer shim, or OpenAPI schema mapping appears at any hop: `int` and `Guid` are the
  values on the wire and in the store.
- **The wrapper-struct alternative is deliberately deferred, not planned.** The `StronglyTypedIds`
  folders exist as the location wrappers would occupy, and today they hold no types. That is the
  current reality: a considered option left unbuilt, not scheduled work.

## Rationale
- **Readable signatures at zero runtime cost.** `GetRepository<User, UserIdentifierType>()` reads as
  intent while the CLR sees a plain `int`. There is no allocation, boxing, or wrapper indirection per
  identifier.
- **No persistence or transport friction.** `int` and `Guid` are natively understood by EF Core, the
  SQL provider, `System.Text.Json`, and the OpenAPI schema generator, so an identifier needs no value
  converter, no `JsonConverter`, and no schema customization anywhere in the trace above. A wrapper
  struct would demand one (or several) at every one of those boundaries.
- **The generic base infrastructure already carries the type parameter.** Because `BaseEntity<TId>`,
  `IBaseEntity<TId>`, the repository handle, `IBaseDTO<TId>`, the per-entity `IEntityDTOMapper<...,TId>`
  (ADR-001), and the generic entity query surface (ADR-034) are all parameterized by the identifier
  type, the alias slots in with no extra plumbing.
- **Per-module declaration mirrors the ownership boundary.** Each module declaring its own aliases in
  its `.Shared` project keeps identity definitions with the module that owns them, consistent with
  database-per-service ownership (ADR-006).

## Trade-offs
- **No compile-time protection against swapping same-typed identifiers.** An alias is a type synonym,
  not a distinct type. Because most aliases resolve to `int`, the compiler will not stop code from
  passing a `SessionIdentifierType` where a `UserIdentifierType` is expected. The `Guid`-backed
  `SpeakerIdentifierType` is guarded only incidentally, because its underlying type differs, not by
  design. This is the exact safety a wrapper struct would buy and that this decision forgoes.
- **The alias is documentation-strength, erased at compile time.** Reflection, tooling, serialized
  payloads, and the OpenAPI document all see `int`/`Guid`; the alias name never survives the build.
- **Convention, not a build-gated invariant.** Unlike many framework rules that are enforced by
  fitness functions (ADR-015), nothing gates that a new entity uses an alias rather than a bare `int`,
  or that the alias is declared in the right `.Shared` project. It rests on author discipline and
  review.
- **Revisiting the trade would be a broad change.** Adopting wrapper structs later would touch every
  generic call site plus add converters and JSON/OpenAPI handling, which is precisely the friction
  being avoided. The empty `StronglyTypedIds` folders mark where they would live if that cost were ever
  judged worth paying.

## Related
ADR-001 (the per-entity DTO mappers are parameterized by this identifier type, `IEntityDTOMapper<TEntity, TEntityDTO, TIdentifierType>`), ADR-034 (the generic entity controllers and query contract ride on the same identifier type parameter), ADR-006 (aliases are declared per module in the module's own `.Shared` project, matching database-per-service ownership), ADR-015 (the contrast: this convention is not fitness-enforced, unlike the invariants that gate the build).
