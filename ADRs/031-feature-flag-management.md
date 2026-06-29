# ADR-031: Config-Driven Feature Flags with Dual-Surface Enforcement

## Status
Accepted (2026-06-27).

## Context
The apps need to decouple *release* from *deploy*: ship code dark, flip a kill switch, or roll a feature
out to a percentage of users without a redeploy. A flag has to be enforceable at **two** different points
of the request path — the HTTP edge (an MVC action) and inside the CQRS pipeline (a command or query) —
because a feature can be reachable from either. ADR-014 already names a `FeatureGate` decorator as the
outermost slot of the command/query pipeline, but it only decides the *decorator ordering*; it does not
decide the provider, the controller-edge surface, the disabled-response convention, or the rollout
filters. This ADR records those.

## Decision
Standardize on **`Microsoft.FeatureManagement`**, configured from the `"FeatureManagement"` configuration
section and registered once in `AddAPI`
(`services.AddFeatureManagement()` + `services.AddSingleton<IDisabledFeaturesHandler,
DisabledFeatureHandler>()`, `MMCA.Common.API/DependencyInjection.cs:73-74`), with the built-in
**Percentage / TimeWindow / Targeting** filters available for progressive rollout. The same flag *name*
is enforced at two independent surfaces:

- **HTTP edge:** `[FeatureGate("X")]` (`Microsoft.FeatureManagement.Mvc`) on a controller or action. When
  `X` is off, `DisabledFeatureHandler` returns an **RFC 9457 ProblemDetails `404`** ("Feature not
  available"), matching the standard `ApiControllerBase.HandleFailure` error shape.
- **CQRS pipeline:** a command/query implements `IFeatureGated` (exposing `FeatureName`). The
  `FeatureGateCommandDecorator` / `FeatureGateQueryDecorator` — the **outermost** decorator (ADR-014) —
  checks `IFeatureManager.IsEnabledAsync(FeatureName)` and, when off, short-circuits with
  `Error.NotFoundError("Feature.Disabled", …)` (`ErrorType.NotFound`) **before** any logging, caching,
  validation, or transaction work.
- **Disabled = `404` (NotFound), never `403`.** Both surfaces return not-found, so a disabled feature is
  indistinguishable from a nonexistent one — it hides the feature's existence rather than advertising a
  forbidden capability.
- **Flag names are module constants** (`CatalogFeatures` / `SalesFeatures` in Store,
  `ConferenceFeatures` / `EngagementFeatures` in ADC) that match keys in each service's
  `"FeatureManagement"` config, so a flag flips at config + restart, not at deploy. The framework itself
  uses `[FeatureGate]` (e.g. the notification controllers), and the decorators ship with unit tests.

## Rationale
- **Release decoupled from deploy.** A kill switch or a percentage rollout becomes a configuration change,
  not a code change — the central reason feature management exists.
- **Two surfaces because the enforcement points see different request shapes.** Gating both the edge
  *and* the handler with one flag name keeps controller and use case in agreement, so a disabled feature
  is unreachable from either entry instead of leaking through the one that was missed.
- **The `404` convention reuses the existing edge.** Both surfaces emit the same Result→ProblemDetails
  not-found shape (ADR-013), so a disabled feature looks like any other not-found and leaks nothing about
  hidden functionality.

## Trade-offs
- **The two enforcement points must agree.** A flag gated on the controller but not the handler (or vice
  versa) is a half-protected feature; no fitness rule asserts both are wired, so coherence is a
  convention/audit concern.
- **Flag debt.** Every flag is a branch that must eventually be removed; the framework provides no expiry
  or staleness check.
- **Per-service configuration.** The same flag name must be present in each service that enforces it. A
  missing key resolves to **disabled** (`IsEnabledAsync`'s default) — fail-safe for a kill switch, but it
  will silently hide a feature you meant to ship if the key is forgotten.
- **Rollout state is per instance unless a context is wired.** Percentage/Targeting bucketing is
  evaluated locally, so consistent assignment across replicas/users needs a deliberate targeting context;
  out of the box the rollout is per-process.

## Related
ADR-014 (the decorator pipeline whose outermost slot `FeatureGate` fills, and the ordering that puts it
first), ADR-013 (the `Result` / `Error` and ProblemDetails edge the disabled responses reuse),
ADR-019 / ADR-020 / ADR-021 / ADR-026 (the other opt-in, audit-the-inventory capabilities).
