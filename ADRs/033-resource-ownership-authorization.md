# ADR-033: Resource-Ownership Authorization (Row-Level + Action Filter)

## Status
Accepted (2026-06-30).

## Context
ADR-020 added a permission (capability) layer over RBAC: it answers "what may this **role** do",
resolving a role to a permission so an endpoint can require a capability instead of a role name. It
explicitly scoped out the orthogonal question, "is this **my** order", recording that "per-resource
ownership (a customer may read only their own data) stays a separate concern (`OwnerOrAdminFilter`),
and a route needing both composes the two" (`ADRs/020-permission-based-authorization.md:71`,
`020-permission-based-authorization.md:72`).

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
enforcement points keyed on the caller's `customer_id` claim and a shared admin bypass.

- **Single-resource action filter.** `OwnerOrAdminFilter`
  (`Source/Presentation/MMCA.Common.API/Authorization/OwnerOrAdminFilter.cs:17`) is a sealed
  `IAsyncActionFilter` taking `ICurrentUserService`. It short-circuits to the action for admins
  (`OwnershipHelper.IsAdmin`, `OwnerOrAdminFilter.cs:25`); otherwise it reads the caller's
  `customer_id` claim via `GetClaimValue<int>("customer_id")` (`OwnerOrAdminFilter.cs:31`) and returns
  `ForbidResult` (HTTP 403) if the claim is missing (`OwnerOrAdminFilter.cs:33`) or if the route `id`
  parses to an int that does not equal the claim (`OwnerOrAdminFilter.cs:39`, `OwnerOrAdminFilter.cs:44`).
  A matching (or non-int) route id falls through to the action (`OwnerOrAdminFilter.cs:48`). It is
  registered scoped by `AddAPI` (`Source/Presentation/MMCA.Common.API/DependencyInjection.cs:68`) and
  applied per controller as `[ServiceFilter(typeof(OwnerOrAdminFilter))]`.
- **Collection ownership specification.** `OwnershipHelper`
  (`Source/Presentation/MMCA.Common.API/Authorization/OwnershipHelper.cs:10`) is a static helper.
  `GetOwnershipSpecification<TSpec, TId>` returns `null` for admins (`OwnershipHelper.cs:41`), and
  otherwise reads the caller's id claim (`GetClaimValue<TId>(claimType)`, `OwnershipHelper.cs:46`) and
  builds a `Specification` via the supplied factory (`OwnershipHelper.cs:47`); a convenience overload
  defaults the claim to `"customer_id"` (`OwnershipHelper.cs:59`, `OwnershipHelper.cs:63`). The returned
  spec is a `Specification<TEntity, TId>` (`Source/Core/MMCA.Common.Domain/Specifications/Specification.cs:15`)
  whose `Criteria` expression (`Specification.cs:23`) the existing query pipeline (`IEntityQueryService`)
  translates to SQL, so a non-admin list query returns only the caller's rows. A `null` spec (admin)
  applies no filter.
- **Admin is the single bypass on both.** `OwnershipHelper.IsAdmin` compares
  `ICurrentUserService.Role` (`Source/Core/MMCA.Common.Application/Interfaces/Infrastructure/ICurrentUserService.cs:18`)
  to `"Admin"` case-insensitively (`OwnershipHelper.cs:15`, `OwnershipHelper.cs:18`). Both enforcement
  points consult it, so an admin sees and touches any resource through either path.
- **Two failure shapes, by design.** The single-resource filter denies with 403 (`ForbidResult`); the
  collection path never 403s, it returns a filtered (possibly empty) result set. Both flow through the
  caller's normal `Result`/HTTP edge (ADR-013), not exceptions.

**Adoption.** MMCA.Store wires both in production. The filter guards
`MMCA.Store/.../Sales.API/Controllers/ShoppingCartsController.cs:37` and
`MMCA.Store/.../Identity.API/Controllers/CustomersController.cs:32` as a `[ServiceFilter]`. The
ownership specification scopes list/get queries:
`ShoppingCartsController` builds a `ShoppingCartByCustomerSpecification`
(`MMCA.Store/.../Sales.API/Controllers/ShoppingCartsController.cs:52`,
`MMCA.Store/.../Sales.Application/ShoppingCarts/Specifications/ShoppingCartByCustomerSpecification.cs:19`,
which filters by `Id` because a cart is keyed by customer, `ShoppingCartByCustomerSpecification.cs:23`)
and `OrdersController` builds an `OrdersByCustomerSpecification`
(`MMCA.Store/.../Sales.API/Controllers/OrdersController.cs:52`,
`MMCA.Store/.../Sales.Application/Orders/Specifications/OrdersByCustomerSpecification.cs:13`, filtering
by `CustomerId`, `OrdersByCustomerSpecification.cs:17`), passing it into each query (for example
`OrdersController.cs:67`). `OrdersController` does not use the class-level filter for its mutating
routes; it runs an explicit per-mutation ownership check, `ValidateOwnershipAsync`
(`OrdersController.cs:291`), that reuses `OwnershipHelper.IsAdmin` (`OrdersController.cs:50`) and
deliberately returns **404 NotFound** rather than 403 so it does not reveal that another customer's
order exists (`OrdersController.cs:289`, `OrdersController.cs:314`).

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
- **Claim-based ownership trusts the token.** Both points key on the `customer_id` claim being present
  and correct, so their correctness depends entirely on the upstream token validation (ADR-004); a
  missing claim 403s (filter) or yields a `null` spec (helper, which for a non-admin returns `null` and
  therefore no scoping, so callers must not treat a missing claim as "admin").
- **The filter assumes route id equals the owning id.** `OwnerOrAdminFilter` compares the `id` route
  value directly to `customer_id` (`OwnerOrAdminFilter.cs:39`), which holds where the resource is keyed
  by the customer (the cart, the customer profile) but not where a resource has a separate id and a
  foreign-key owner; those (orders) need the spec or an explicit per-id check instead.
- **This is ownership, not ABAC.** It answers "is this row mine" against a single id claim with an admin
  override; it does not evaluate arbitrary resource attributes, hierarchies, or delegated access. A
  richer policy would be a different mechanism, not a parameter on this one.

## Related
ADR-020 (the role/permission RBAC layer this complements, and whose explicit
`020-permission-based-authorization.md:71` scope-out this fills), ADR-034 (the generic entity query
pipeline / `IEntityQueryService` the collection-scoping `Specification` slots into), ADR-013 (failures
surface as `Result`/HTTP at the edge, the filter as a 403 `ForbidResult`), ADR-004 (the validated
principal and `customer_id` claim both enforcement points trust).
