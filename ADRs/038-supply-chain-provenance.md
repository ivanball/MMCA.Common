# ADR-038: Supply-Chain Provenance (SBOM Release Gate + Lock Files + Vulnerability Audit)

## Status
Accepted (2026-07-06).

## Context
MMCA.Common is a published framework: it packs its NuGet packages and pushes them to GitHub Packages
on every `v*` tag (release.yml:59-60), where the two production apps and the reference seed consume
them. A published framework is a supply-chain amplifier: a vulnerable, substituted, or unreproducible
dependency does not stay in one repo, it ships downstream to every consumer. Rubric §32 (Dependency &
Supply-Chain Management) is weighted higher for exactly this reason: its default weight rises to 3 for
a published framework such as MMCA.Common (ArchitectureEvaluationCriteria.md:817), and it asks for
provenance and integrity (SBOM, lock files, trusted sources only) on top of the versioning hygiene
§15 covers (ArchitectureEvaluationCriteria.md:808).

ADR-016 answers two dependency-governance questions (how the packages version and roll out, and the
MassTransit-v8 license pin), but it deliberately stops there. The four controls that actually
establish provenance and integrity, an SBOM release gate, committed lock files, a CI vulnerability
audit, and package source mapping, already live in the release and CI workflows,
`Directory.Build.props`, and `nuget.config`, and are summarized for consumers in SECURITY.md
(SECURITY.md:32-41). No ADR owned them as a single coherent posture. This record does, and it exceeds
ADR-016's scope: ADR-016 gestures at lock files as sweep mechanics, this ADR owns supply-chain
integrity as the decision.

## Decision
Treat supply-chain integrity as a set of build-gating controls, the same invariant-over-discipline
posture ADR-015 applies to architecture rules. Four controls, each a hard gate:

1. **A CycloneDX SBOM is a hard release gate.** The release workflow installs the CycloneDX tool and
   generates a JSON software bill of materials for the whole solution into `./sbom` (release.yml:45-49).
   It then fails the release (`exit 1`) when generation produced no output
   (`test -n "$(ls -A ./sbom ...)"`, release.yml:50), and the upload step sets `if-no-files-found:
   error` (release.yml:52-57). This step was promoted from `continue-on-error` (its tooling-validation
   phase) to a blocking gate (release.yml:41-44), so every published version ships a verifiable SBOM or
   no packages are pushed.

2. **NuGet lock files are committed for reproducible restores.** `RestorePackagesWithLockFile` is set
   repo-wide (Directory.Build.props:8), so each project records its full resolved transitive graph in a
   committed `packages.lock.json` (for example Source/Core/MMCA.Common.Domain/packages.lock.json).
   Restore in CI and in release runs against that committed graph (ci.yml:30-31, release.yml:25-26), so
   the versions CI vets and the release packs are the ones on record.

3. **CI fails on any non-suppressed vulnerable package.** The audit step runs
   `dotnet list MMCA.Common.slnx package --vulnerable --include-transitive` (ci.yml:36-38) and fails the
   build (`exit 1`) on any vulnerable-package row (ci.yml:45-51). Accepted advisories are the sole
   exception, and their single source of truth is the `NuGetAuditSuppress` list in
   `Directory.Build.props`. Because `dotnet list --vulnerable` ignores `NuGetAuditSuppress`, the step
   re-derives that accept-list itself by reading the `GHSA-*` ids out of `Directory.Build.props`
   (ci.yml:40-48). The accepted-advisory list is currently empty: the one prior entry, the SQLite
   advisory GHSA-2m69-gcr7-jv3q (CVE-2025-6965), was suppressed from 2026-06-19 while SQLitePCLRaw
   shipped no patched build. SQLitePCLRaw 2.1.12 (published 2026-07-14) fixed it, so the suppression
   was removed on 2026-07-20 and replaced with a direct fix: a `SQLitePCLRaw.bundle_e_sqlite3` pin at
   2.1.12 in `Directory.Packages.props`, referenced directly by `MMCA.Common.Infrastructure` so the
   patched version flows to consumers through the published package graph, the same pattern used for
   the MessagePack pin. This complements the build-time audit: `NuGetAudit` with `NuGetAuditMode=all`
   (Directory.Build.props:9-10)
   under repo-wide `TreatWarningsAsErrors` (Directory.Build.props:7) already promotes an advisory to a
   build failure, and the CI step adds a solution-wide, transitive check carrying an auditable
   accept-list.

4. **Package source mapping pins every dependency to an explicit feed.** `nuget.config` clears
   inherited sources and declares nuget.org only (nuget.config:9-12), then a `packageSourceMapping`
   block routes package pattern `*` to that source (nuget.config:13-17). This is the
   dependency-confusion and typosquat defense: a package from any other feed cannot be silently
   substituted (nuget.config:3-8). MMCA.Common needs only the single `* -> nuget.org` mapping because it
   publishes the `MMCA.*` packages rather than consuming them (nuget.config:6-7), so no GitHub Packages
   token is required to build or restore it.

## Rationale
- **Provenance is a gate, not a document.** A hard-failing SBOM step means the bill of materials
  cannot silently go missing on a release: the artifact is produced or the release stops
  (release.yml:50, release.yml:57). That is the §32 provenance criterion enforced, not merely asserted
  (ArchitectureEvaluationCriteria.md:808).
- **One accept-list, re-applied where the tool ignores it.** The audit keeps `Directory.Build.props`
  as the only place an advisory is accepted, and re-reads that file in CI precisely because
  `dotnet list --vulnerable` does not honor `NuGetAuditSuppress` (ci.yml:40-42). A reviewer sees every
  accepted advisory in one place, with a dated rationale (Directory.Build.props:19-25), rather than a
  blanket suppression.
- **Build-gates-invariants, at the supply-chain layer.** SBOM, audit, and source mapping turn
  "remember to check the dependencies" into red builds, the same lever ADR-015 uses for the layer and
  event rules and ADR-016 uses for the MassTransit pin.
- **Reproducibility feeds the audit.** Committed lock files (Directory.Build.props:8) record the exact
  transitive graph the audit and SBOM run against, so the release ships the versions CI actually
  vetted.

## Trade-offs
- **The SBOM is generated and archived, not yet signed or attested.** The gate proves a bill of
  materials exists for each release (release.yml:50); it does not add cryptographic attestation or
  signature verification of the pushed packages. That is a possible follow-up, not a claim made here.
- **Accept-list drift is possible.** A suppressed advisory (Directory.Build.props:26-27) silences the
  audit for that id until someone removes it; the inline "re-check periodically" note
  (Directory.Build.props:24-25) is a review reminder, not an automated expiry.
- **Audit granularity is text-matched.** The CI step matches vulnerable rows and `GHSA-*` ids by
  parsing tool output (ci.yml:43-48). It is deliberately simple and depends on the `dotnet list`
  output shape rather than a structured feed.
- **Source mapping constrains where packages come from.** Restricting to nuget.org (nuget.config:15)
  is the point, but it means adding a dependency from any other feed is a deliberate `nuget.config`
  edit, not an ambient possibility.

## Related
ADR-016 (lockstep versioning + the MassTransit-v8 license pin; this record extends dependency
governance from versioning and licensing into supply-chain provenance and integrity), ADR-015
(architecture invariants enforced as build-gating fitness functions; the same gate-the-build posture
applied here to dependencies), ADR-010 (integration-event schema versioning; another release-discipline
control that turns a contract into an enforced signal). See SECURITY.md ("Dependency & supply-chain
security", SECURITY.md:32-41) for the consumer-facing summary and rubric §32
(ArchitectureEvaluationCriteria.md:799-817) for the evaluation criteria.
