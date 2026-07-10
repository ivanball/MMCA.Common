# ADR-020: Permission-Based Authorization Layered over Roles

## Status
Accepted (2026-06-25, amended 2026-07-10).

## Context
Authorization started as pure role-based access control (RBAC). Endpoints declared the role they
required with `[Authorize(Policy = ...)]` against named policies: `RequireOrganizer`, `RequireAttendee`,
and `RequireAdmin` each call `RequireRole(...)`, while `RequireAuthenticated` calls
`RequireAuthenticatedUser()`. That couples every role-guarded endpoint to a role *name*, and it has two
concrete failure modes:

- **Reshaping who-can-do-what means editing endpoints.** Splitting a coarse role into a narrower one
  (for example, a content editor who may curate the session catalog but not manage events, rooms, or
  the organizer session-selection workflow) forces either touching every affected `[Authorize]`
  attribute or granting the new role more than it should have.
- **The capability a route needs is implicit.** "Requires Organizer" does not say *which* capability
  the route exercises, so the same capability guarded in several places drifts.

We wanted endpoints to depend on **capabilities** (fine-grained permissions) rather than role names,
with the role-to-capability mapping declared in one place, without discarding the existing role
policies or forcing every consumer to adopt the new model.

## Decision
Add a **permission (capability) layer over RBAC**, opt-in and backward-compatible.

- **A central registry maps roles to permissions.** `IPermissionRegistry` / `PermissionRegistry`
  (`MMCA.Common.Shared.Auth`) answer "does any of these roles grant this permission?" from an
  immutable `FrozenDictionary` snapshot. It is the single place that knows which roles confer which
  capabilities, so endpoints stay decoupled from role names (role keys compared case-insensitively,
  permission values ordinally).
- **Modules declare grants additively.** `AddPermissions(builder => builder.Grant(role, ...))`
  (`MMCA.Common.API`) accumulates grants into one shared `PermissionRegistryBuilder`; grants from
  different modules union, so each module declares only the permissions it owns. The registry is
  built lazily on first resolve, after every module has contributed.
- **Endpoints require a capability, not a role.** `[HasPermission("conference:sessions:manage")]`
  (a `HasPermissionAttribute : AuthorizeAttribute`) names the capability. It maps to an on-demand
  authorization policy named `perm:{permission}`: `PermissionPolicyProvider` materializes that policy
  (`RequireAuthenticatedUser()` + a `PermissionRequirement`) the first time it is requested, so there
  is no per-permission named-policy registration. Every non-`perm:` policy name falls through to
  `DefaultAuthorizationPolicyProvider`, leaving the named role policies untouched.
- **Two grant sources.** `PermissionAuthorizationHandler` succeeds when the principal carries the
  permission directly (an explicit `AuthClaimTypes.Permission` claim, `"permission"`) **or** holds a
  role the registry grants it (roles gathered from `ClaimTypes.Role`, `"role"`, or `"roles"` so it
  works whether or not inbound-claim mapping is on). Baking permissions into the token is therefore
  optional: role-derived resolution is the default.
- **Inert until adopted.** `AddAuthorizationPolicies()` always wires the handler, the policy provider,
  and an empty shared registry, so any host that configures authentication gets the mechanism for
  free, but it grants nothing beyond explicit claims until a host calls `AddPermissions(...)`. The
  existing role policies keep working unchanged.

Adoption is asymmetric and that is intentional: ADC's Conference module defines seven capabilities
(including a curation subset granted to a new `RoleNames.ContentEditor`), its Engagement module
defines `engagement:live:manage` (granted to `Organizer` and `Admin`, gating the conference-day
live-poll management endpoints) and its Identity module defines `identity:users:read`; MMCA.Store
has not adopted it and still authorizes by role policy only. The registry, handler, policy provider,
and the ADC grant tables (Conference and Engagement) are all covered by tests.

## Rationale
- **Capabilities decouple endpoints from roles.** A route says what it *does*
  (`conference:sessions:manage`), and who may do it is a registry decision, so adding `ContentEditor`
  with a strict subset of the organizer's capabilities is a grant change, not an endpoint sweep. That
  subset is exactly the distinction role checks cannot express cleanly and is what makes the
  indirection earn its keep.
- **One place to read and change policy.** Grants live in module registration, not scattered across
  controllers; the registry is immutable and thread-safe once built.
- **Additive and safe to ignore.** Per-module union means no module needs to know another's
  permissions, and the empty-registry default plus the fallback provider make the feature a no-op for
  consumers that never opt in, satisfying the framework's non-breaking `[C->A]` rollout rule
  (ADR-016).

## Trade-offs
- **It is still RBAC, not ABAC.** The model resolves role to permission; it does not evaluate
  resource or attribute conditions. Per-resource ownership ("a customer may read only their own
  data") stays a separate concern (`OwnerOrAdminFilter`), and a route needing both composes the two.
- **Another indirection to keep honest.** A capability check is only as good as its grant; a missing
  or wrong `Grant(...)` silently denies or over-permits. The registry tests are the mitigation, but
  the mapping is declared in code and is not enforced by a fitness rule.
- **Opt-in per endpoint.** A route that should check a capability but still uses a role policy gets
  none of the benefit, the same audit-the-inventory caveat as ADR-005 (`IAnonymizable`) and ADR-017
  (`[Idempotent]`).
- **Grants must be registered before the host is built.** The registry is built once on first
  resolve; an `AddPermissions(...)` call after it has been materialized is not seen. Permission
  strings are also stringly-typed, mitigated by exposing them as constants (for example
  `ConferencePermissions`).

## Related
ADR-004 (the authenticated principal and claims this keys on, including the optional `permission`
claim), ADR-008 (each extracted service authorizes independently, so the registry is wired per
service host), ADR-013 / ADR-014 (capability checks at the edge keep the handlers thin).
