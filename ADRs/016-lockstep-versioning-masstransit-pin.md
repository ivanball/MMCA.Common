# ADR-016: Lockstep Package Versioning and the MassTransit-v8 Pin

## Status
Accepted (2026-07-15).

## Context
MMCA.Common publishes its `MMCA.Common.*` NuGet package set (see `../FACTS.md` for the authoritative
list and count) consumed by three downstream repos: the two
production apps (Store, ADC) and the reference seed Helpdesk (which defaults to local-source mode but
declares the same `MMCA.Common.*` package versions in its own `Directory.Packages.props`).
Two related governance questions had no recorded answer:

1. **How do the packages version and roll out?** Independent per-package versions invite a
   combinatorial compatibility matrix ("which `.Domain` works with which `.Infrastructure`?"), and a
   phased / opt-in rollout across consumers invites long-lived version skew between the apps and the
   framework.
2. **Can dependencies float?** A routine "update all NuGet packages" sweep repeatedly pulled
   `MassTransit` to v9, which requires a commercial license (`MT_LICENSE`) and crashes every
   broker-enabled service at startup.

## Decision
1. **Version the whole `MMCA.Common.*` package set in lockstep.** All packages share one version (MinVer, derived from
   a single `vX.Y.Z` git tag); a release tags every package (see `../FACTS.md` for the authoritative list and count)
   at the same version. SemVer and the
   breaking-change policy live in `VERSIONING.md`.
2. **Sweep every consumer in one pass, with no phased rollout.** A framework change ships and all
   consumers bump every `MMCA.Common.*` entry in their `Directory.Packages.props` together.
   The two production apps (Store, ADC) now **commit NuGet lock files** (`RestorePackagesWithLockFile`,
   R7/TD-01), so each one's sweep is a version bump **plus a restore that regenerates its lock files**
   (the same `audit=all` / `--force-evaluate` mechanics Common already uses); Helpdesk, which defaults
   to local-source mode, keeps no lock files. Framework-to-app (`[C->A]`) changes are designed to be
   non-breaking so the one-pass sweep is safe.
3. **Pin MassTransit to v8 by policy, enforced by a fitness function.** `DependencyVersionTests` parses
   `Directory.Packages.props` and fails the build if the MassTransit major reaches 9 (ADR-015). The pin
   is asserted only in MMCA.Common, where the version is actually declared; Store, ADC, and Helpdesk
   inherit it transitively through `MMCA.Common.Infrastructure` and deliberately do **not** subclass the
   test (the default rule would fail parsing a pin they do not declare).

## Rationale
- **One version, one compatibility story.** Lockstep removes the N-package matrix: "everything on
  `vX.Y.Z`" is the only supported combination, which is the right trade for a small team.
- **No skew.** Sweeping all consumers at once keeps the framework and both apps converged rather than
  drifting across releases.
- **The pin is a build gate, not a comment.** A floating-version sweep would otherwise silently
  reintroduce the licensed v9 and break startup; a fitness function is the only thing that reliably
  stops it (which is exactly the invariant-over-discipline posture of ADR-015).

## Trade-offs
- A consumer cannot adopt a single package in isolation: it takes the whole set at the new version.
- Lockstep will bump a package whose code did not change (acceptable: the version means "compatible
  with this set," not "this package changed").
- The v8 pin forgoes MassTransit v9 features until a licensing decision is made; bumping it is a
  deliberate, multi-step change, not a one-line version edit.

## Related
ADR-015 (the fitness function that enforces the pin), ADR-003 / ADR-006 (MassTransit is the broker
transport behind the outbox and database-per-service flows).
