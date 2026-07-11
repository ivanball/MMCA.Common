# Versioning & Breaking-Change Policy

MMCA.Common publishes fifteen NuGet packages that are versioned and released **together** as a
single unit. They share one version number so a consumer never has to reason about cross-package
compatibility. (`MMCA.Common.UI.Maui` rides the same tag but is built and packed by a dedicated
windows job in `release.yml`, since its MAUI target frameworks cannot build on the ubuntu runner.)

## Semantic Versioning

Versions follow [SemVer 2.0](https://semver.org/) — `MAJOR.MINOR.PATCH`:

- **MAJOR** — a breaking change to the public API or a behavior contract (see below).
- **MINOR** — backward-compatible new capability.
- **PATCH** — backward-compatible bug fix.

Versions are produced by **MinVer** from annotated git tags of the form `vMAJOR.MINOR.PATCH`
(e.g. `v1.51.0`). Untagged builds get a height-based pre-release suffix. There is no hand-edited
version property; tag the commit and the release workflow packs and pushes.

## What counts as breaking

A **MAJOR** bump is required for any of:

- Removing or renaming a public type/member, or changing a signature.
- Changing the meaning of an existing configuration key, or changing a default in a way that
  alters runtime behavior for an unchanged consumer (e.g. the `Outbox:RetentionDays` default —
  call these out in [CHANGELOG.md](CHANGELOG.md) under **Changed (Behavior)**).
- Tightening a base-class contract (e.g. making a virtual member abstract, or a factory now
  returning `Result<T>` where it returned a bare entity).
- Removing or narrowing a supported transport/provider/data-source engine.

Additive, opt-in changes (new settings with safe defaults, new overloads, new interfaces) are
**MINOR**.

## Consumer rollout

Per project convention, framework upgrades are **swept across all consumers in one pass** —
there are no opt-in flags or phased rollouts for a MMCA.Common change. When a release contains a
breaking change, the consuming repos (MMCA.ADC, MMCA.Store) are updated in the same change set.

## Deprecation

Prefer deprecation over removal where possible: mark the old API `[Obsolete("…", error: false)]`
for at least one MINOR release, document the replacement in the CHANGELOG, then remove in the next
MAJOR.

## Supply chain

- All package versions are centrally pinned (`Directory.Packages.props`).
- NuGet **lock files** are committed for reproducible restores.
- `MassTransit` is pinned to **v8** by policy (v9 needs a commercial license) — enforced by a
  fitness test, not just a comment.
- A **CycloneDX SBOM** is produced at release; dependency vulnerabilities are audited in CI.
