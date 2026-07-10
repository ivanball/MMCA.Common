# ADR-033: Resource-Ownership Authorization (Row-Level + Action Filter)

## Status
Accepted (2026-07-02).

## Context
ADR-020 added a permission (capability) layer over RBAC: it answers "what may this **role** do",
resolving a role to a permission so an endpoint can require a capability instead of a role name. It
explicitly scoped out the orthogonal question, "is this **my** order", recording that "per-resource
ownership (a customer may read only their own data) stays a separate concern (`OwnerOrAdminFilter`),
and a route needing both composes the two" (`ADRs/020-permission-based-authorization.md:72`,
`020-permission-based-authorization.md:73`).

That carve-out names a mechanism that already ships in framework code but had no decision record of
its own. RBAC and permissions are principal-scoped: a customer with the Customer role may read orders,
but that role says nothing about *which* orders. Two endpoint shapes need a different, resource-scoped
check that the role/permission model cannot express:

- **Single-resource routes** (`GET /orders/{id}`, `GET /customers/{id}`): the id in the URL identifies
  one resource, and a non-admin caller must be denied if that resource is not theirs.
- **Collection/list routes** (`GET /orders`, `GET /shoppingcarts`): there is no id to check; the result
  set itself must be narrowed to the caller's own rows rather than returning everyone's data.

These are different problems (reject-one vs filter-many) and cannot be one mechanism. This ADR records
the shipped resource-ownership axis that sits beside ADR-020, not inside it.

## Decision
Provide a row/resource-level ownership axis in `MMCA.Common.API` (the `Authorization` folder), with two
enforcement points keyed on the caller's owner claim (`customer_id` by default) and a configurable
bypass role (`Admin` by default).

- **Single-resource action filter.** `OwnerOrAdminFilter`
  (`Source/Presentation/MMCA.Common.API/Authorization/OwnerOrAdminFilter.cs:20`) is a sealed
  `IAsyncActionFilter` whose primary constructor takes `ICurrentUserService` and
  `IOptions<OwnerOrAdminFilterOptions>`. Its ownership vocabulary comes from
  `OwnerOrAdminFilterOptions`
  (`Source/Presentation/MMCA.Common.API/Authorization/OwnerOrAdminFilterOptions.cs:11`), whose defaults
  reproduce the original hard-coded behavior: `OwnerClaimType` `"customer_id"`
  (`OwnerOrAdminFilterOptions.cs:14`), `BypassRole` `"Admin"` (`OwnerOrAdminFilterOptions.cs:17`), and
  `OwnerParameterName` `"id"` (`OwnerOrAdminFilterOptions.cs:24`), so a host that configures nothing
  behaves exactly as before. It short-circuits to the action for the bypass role
  (`OwnershipHelper.IsAdmin(currentUserService, settings.BypassRole)`, `OwnerOrAdminFilter.cs:32`);
  otherwise it reads the caller's owner claim via `GetClaimValue<int>(settings.OwnerClaimType)`
  (`OwnerOrAdminFilter.cs:38`) and returns `ForbidResult` (HTTP 403) if the claim is missing
  (`OwnerOrAdminFilter.cs:40`, `OwnerOrAdminFilter.cs:42`) or if the requested owner parameter resolves
  to an int that does not equal the claim (`OwnerOrAdminFilter.cs:46`, `OwnerOrAdminFilter.cs:49`).
  `TryGetOwnerParameter` (`OwnerOrAdminFilter.cs:58`) reads that parameter from a **route value**
  (`/customers/{id}`) or, when the route lacks it, from a **model-bound query/body argument**
  (`?userId=42`), so the guard now also covers list/query routes that carry the owner as a bound
  argument, not only route ids. An absent or non-int parameter falls through to the action
  (`OwnerOrAdminFilter.cs:53`). It is registered scoped by `AddAPI`
  (`Source/Presentation/MMCA.Common.API/DependencyInjection.cs:68`) and applied per controller as
  `[ServiceFilter(typeof(OwnerOrAdminFilter))]`.
- **Collection ownership specification.** `OwnershipHelper`
  (`Source/Presentation/MMCA.Common.API/Authorization/OwnershipHelper.cs:10`) is a static helper.
  `GetOwnershipSpecification<TSpec, TId>` returns `null` for the bypass role (`OwnershipHelper.cs:45`),
  and otherwise reads the caller's id claim (`GetClaimValue<TId>(claimType)`, `OwnershipHelper.cs:50`)
  and builds a `Specification` via the supplied factory (`OwnershipHelper.cs:51`); a convenience
  overload defaults the claim to `"customer_id"` (`OwnershipHelper.cs:63`, `OwnershipHelper.cs:67`). The
  returned spec is a `Specification<TEntity, TId>` (`Source/Core/MMCA.Common.Domain/Specifications/Specification.cs:15`)
  whose `Criteria` expression (`Specification.cs:23`) the existing query pipeline (`IEntityQueryService`)
  translates to SQL, so a non-admin list query returns only the caller's rows. A `null` spec (bypass
  role) applies no filter.
- **The bypass role is the single override on both.** `OwnershipHelper.IsAdmin`
  (`Source/Presentation/MMCA.Common.API/Authorization/OwnershipHelper.cs:17`) compares
  `ICurrentUserService.Role` (`Source/Core/MMCA.Common.Application/Interfaces/Infrastructure/ICurrentUserService.cs:18`)
  to its `bypassRole` argument (`"Admin"` by default) case-insensitively (`OwnershipHelper.cs:20`). Both
  enforcement points consult it, so a caller in the bypass role sees and touches any resource through
  either path.
- **Two failure shapes, by design.** The single-resource filter denies with 403 (`ForbidResult`); the
  collection path never 403s, it returns a filtered (possibly empty) result set. Both flow through the
  caller's normal `Result`/HTTP edge (ADR-013), not exceptions.

**Adoption.** MMCA.Store wires both in production. The filter guards
`MMCA.Store/.../Sales.API/Controllers/ShoppingCartsController.cs:38` and
`MMCA.Store/.../Identity.API/Controllers/CustomersController.cs:32` as a `[ServiceFilter]`. The
ownership specification scopes list/get queries:
`ShoppingCartsController` builds a `ShoppingCartByCustomerSpecification`
(`MMCA.Store/.../Sales.API/Controllers/ShoppingCartsController.cs:53`,
`MMCA.Store/.../Sales.Application/ShoppingCarts/Specifications/ShoppingCartByCustomerSpecification.cs:19`,
which filters by `Id` because a cart is keyed by customer, `ShoppingCartByCustomerSpecification.cs:23`)
and `OrdersController` builds an `OrdersByCustomerSpecification`
(`MMCA.Store/.../Sales.API/Controllers/OrdersController.cs:53`,
`MMCA.Store/.../Sales.Application/Orders/Specifications/OrdersByCustomerSpecification.cs:13`, filtering
by `CustomerId`, `OrdersByCustomerSpecification.cs:17`), passing it into each query (for example
`OrdersController.cs:68`). `OrdersController` does not use the class-level filter for its mutating
routes; it runs an explicit per-mutation ownership check, `ValidateOwnershipAsync`
(`OrdersController.cs:293`), that reuses `OwnershipHelper.IsAdmin` (`OrdersController.cs:51`) and
deliberately returns **404 NotFound** rather than 403 so it does not reveal that another customer's
order exists (`OrdersController.cs:291`, `OrdersController.cs:316`).

MMCA.ADC's Engagement module is the first host to configure the filter's vocabulary rather than take
the defaults. `AddModuleEngagementAPI`
(`MMCA.ADC/Source/Modules/Engagement/MMCA.ADC.Engagement.API/DependencyInjection.cs:42`) calls
`services.Configure<OwnerOrAdminFilterOptions>(...)` (`DependencyInjection.cs:44`) to point the shared
filter at ADC's own ownership terms: the `user_id` claim its token service emits
(`DependencyInjection.cs:46`), the `Organizer` bypass role (`RoleNames.Organizer`,
`Source/Core/MMCA.Common.Shared/Auth/RoleNames.cs:15`, `DependencyInjection.cs:47`), and the `userId`
query argument its Bookmarks list endpoints bind (`DependencyInjection.cs:48`). The filter type is
unchanged; only the options differ, which is exactly what the options object exists for. The Bookmarks
**delete** keeps a separate DB-backed inline ownership check that returns 404-not-403 (the same
per-mutation, existence-hiding pattern Store's `OrdersController` uses).

## Rationale
- **Reject-one and filter-many are genuinely two mechanisms.** A single-resource route has an id to
  compare, so a short action filter that 403s on a mismatch is the cheapest correct guard. A collection
  route has no id; narrowing it means pushing a predicate into the query, which a filter cannot do
  without re-running the query itself. Forcing both through one abstraction would either over-fetch then
  post-filter (leaky, and breaks paging counts) or fail to scope lists at all.
- **Ownership lives beside RBAC, not inside it.** Role/permission resolution (ADR-020) is a property of
  the principal; ownership is a relation between the principal and a specific row. Keeping them separate
  lets a route compose both (require a capability *and* own the resource) without either model growing a
  resource-condition concept it was not designed for.
- **A `Specification` composes with the existing pipeline.** The row-scope is expressed as a
  `Specification<TEntity, TId>` whose `Criteria` is an EF-translatable expression
  (`Specification.cs:9`, `Specification.cs:23`), so it slots into `IEntityQueryService` alongside
  filtering, sorting, paging, and projection (and can be `And`-composed with other specs,
  `Specification.cs:62`) rather than introducing a parallel query path.

## Trade-offs
- **Opt-in per controller/handler.** Neither point is automatic: a controller that forgets the
  `[ServiceFilter]` or omits the ownership spec from a query leaks across customers, the same
  audit-the-inventory caveat as ADR-019 / ADR-020 / ADR-021. The two-enforcement-point split also means
  one route can guard mutations but forget to scope its list (or vice versa).
- **Claim-based ownership trusts the token.** Both points key on the configured owner claim
  (`customer_id` by default) being present and correct, so their correctness depends entirely on the
  upstream token validation (ADR-004); a missing claim 403s (filter) or yields a `null` spec (helper,
  which for a non-admin returns `null` and therefore no scoping, so callers must not treat a missing
  claim as "admin").
- **The filter assumes the owner parameter equals the owning id.** `OwnerOrAdminFilter` compares its
  configured owner parameter, resolved from either a route value or a model-bound argument, against the
  configured owner claim (`OwnerOrAdminFilter.cs:46`). That holds where the resource is keyed by the
  owner (the cart, the customer profile, a user's own bookmarks) but not where a resource has a separate
  id and a foreign-key owner; those (orders) need the spec or an explicit per-id check instead.
- **This is ownership, not ABAC.** It answers "is this row mine" against a single id claim with an admin
  override; it does not evaluate arbitrary resource attributes, hierarchies, or delegated access. A
  richer policy would be a different mechanism, not a parameter on this one.

## Related
ADR-020 (the role/permission RBAC layer this complements, and whose explicit
`020-permission-based-authorization.md:72` scope-out this fills), ADR-034 (the generic entity query
pipeline / `IEntityQueryService` the collection-scoping `Specification` slots into), ADR-013 (failures
surface as `Result`/HTTP at the edge, the filter as a 403 `ForbidResult`), ADR-004 (the validated
principal and owner claim both enforcement points trust).
