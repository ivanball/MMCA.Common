# Contributing to MMCA.Common

Thanks for taking an interest in the framework. This is a short guide; the full contributor
reference (package layout, layer rules, build/test commands) is [CLAUDE.md](CLAUDE.md).

Participation here is governed by the [Code of Conduct](CODE_OF_CONDUCT.md) (Contributor Covenant
2.1). By taking part you agree to uphold it.

## Commit messages: Scoped Commits

This repo uses [Scoped Commits](https://scopedcommits.com/), not Conventional Commits. The scope
comes first, then a plain description:

```
<scope>: <description>
```

Real examples from the history:

```
release.yml: run publish-maui nuget push under bash (pwsh does not glob *.nupkg)
ADR-045: managed file storage, image normalization, media picking
§27: surface domain rejection messages in ErrorMessages toasts (ADR-027 carve-out)
```

Conventions on top of that:

- **Scorecard remediation** work uses the rubric category as the scope: `§<m>: <summary>`
  (see [the published scorecard](https://ivanball.github.io/docs/governance/common-ArchitectureScorecard.html) for the categories).
- **ADR work** uses the record as the scope: `ADR-NNN: <summary>`.
- Multi-scope changes use an umbrella scope, or list scopes separated by commas.
- Merges and reverts can keep their default format.

Dependabot is configured to follow the same style (`deps:` for NuGet, `ci:` for Actions).

## Before you open a PR

```bash
dotnet build MMCA.Common.slnx -c Release
dotnet test --solution MMCA.Common.slnx -c Release
```

- `TreatWarningsAsErrors` is on globally, with five analyzers at error severity. A clean local
  Release build is the baseline.
- Layer rules are enforced twice: a compile-time MSBuild guard and NetArchTest fitness tests.
  If you move a type between packages or add a project reference, expect both gates to react,
  and update both when the change is deliberate.
- `FACTS.md` is generated and CI-gated. If your change affects the version, package list, ADR
  range, or fitness counts, regenerate it: `dotnet run --project build/facts -- .`
- Every cross-cutting pattern has an Architecture Decision Record explaining why it exists.
  Read the relevant one in [the published ADR index](https://ivanball.github.io/docs/adr/) before changing a pattern it
  describes; substantive pattern changes should update or add an ADR.

## Pull request workflow

`main` is protected. All changes land through a pull request; nobody pushes to `main` directly.

1. Branch from an up-to-date `main` (e.g. `feature/<short-name>` or `fix/<short-name>`).
2. Commit your work (Scoped Commits, above), push the branch, and open a PR against `main`.
3. CI runs automatically. The eight required merge gates are:
   - `build-and-test` (includes the FACTS drift gate, the vuln audit, and the tests)
   - `Build MMCA.Common.UI.Maui (windows, 4 TFMs)`
   - `UI a11y + render smoke (chromium)`, `UI a11y + render smoke (firefox)`, and
     `UI a11y + render smoke (webkit)` - all three engines block (webkit was promoted from
     advisory to required on 2026-07-16)
   - `coverage` (the unit-tier line-coverage floor)
   - `Consumer source build (Helpdesk)` - the cross-repo canary (see below)
   - `Performance gate (BenchmarkDotNet Short + baseline verify)` - `build/perfgate` checks the
     benchmark results against the committed `Tests/Performance/perf-baseline.json` (allocation
     ceilings + ratio floors). Moving a number deliberately means updating the baseline in the
     same PR; raising a ceiling to silence a red gate defeats it.

   The automated Claude review also comments on every PR (advisory, not a gate).

   This list is a convenience copy. The live ruleset is authoritative: read it with
   `gh api repos/ivanball/MMCA.Common/branches/main/protection --jq '.required_status_checks.contexts[]'`,
   and prefer that output over this file whenever the two disagree.
4. Merge once the required checks are green. The ruleset requires **0 approving reviews today**
   (transitional, while the team is small); a maintainer may self-merge a green PR. This will
   ratchet to 1 required approval once a second reviewer is available.

If the **FACTS drift gate** goes red, regenerate and commit the file on your branch:
`dotnet run --project build/facts -- .` then `git add FACTS.md`. Do not hand-edit the computed
values.

## Validating a cross-repo change

MMCA.Common publishes its public API as versioned NuGet packages; consumers (MMCA.ADC,
MMCA.Store, MMCA.Helpdesk) only pick up a change **after** a release + lockstep sweep. Two things
let you catch a breaking change before it ships:

- **The Helpdesk source-build canary** (CI) builds MMCA.Helpdesk against this branch's framework
  *source* (`UseLocalMMCA`), so an API break in your PR fails the PR instead of the next release.
- **Local source mode** lets you iterate a consumer against your Common branch with no token. It has
  two traps that look like code bugs; both are covered in the next section.

## Local source-mode development

Normally a consumer (MMCA.ADC, MMCA.Store, MMCA.Helpdesk) resolves `MMCA.Common.*` as released
NuGet packages, so a framework change only reaches it after a release. **Local source mode** swaps
those `PackageReference`s for `ProjectReference`s pointing at your working copy of the framework, so
a consumer builds against the code you are editing right now, with no GitHub Packages token.

### Turning it on

Clone the consumer as a **sibling** of this repo (the relative path is what makes it work):

```
C:\Projects\MMCA\
  MMCA.Common\
  MMCA.Helpdesk\
```

Then, in the consumer repo:

```bash
cp local.props.template local.props
```

That sets `UseLocalMMCA=true` and points at `../MMCA.Common/Source/`. `local.props` is gitignored,
so the switch stays local to your machine. MMCA.Helpdesk is the exception: it ships `local.props`
checked in and is in source mode by default, which is what lets the CI canary build it against a PR.

To go back to package mode, delete (or rename) `local.props` and restore.

### Rebuild Common in Debug first

**Symptom:** you add a member to a framework type, rebuild only the consumer, and the compiler
reports `CS0103: The name 'X' does not exist in the current context` (or `CS0117` / `CS1061`) against
the member you just added. The code is correct and the file is right there on disk.

**Cause:** the `ProjectReference` points outside the consumer's solution, so the build binds the
framework's **last-built Debug reference assembly** rather than recompiling your edit.

**Fix:** build the framework project in Debug before rebuilding the consumer.

```bash
dotnet build <path to the MMCA.Common project you changed> -c Debug
```

This is not a code bug and no amount of cleaning the consumer fixes it. Note the configuration: a
`-c Release` build of the framework does not refresh the **Debug** ref assembly the consumer binds.

### A green source-mode build is not proof CI is green

Source mode and package mode do not fail identically. A local source-mode build can pass (even in
Release) while CI fails in package mode on an analyzer or a restore rule that only applies to a
packaged reference: analyzers flowing from a package, `NU1605` downgrades, lock-file drift, and pack
errors (`NU5xxx`) are all invisible to a source-mode build. The `package-consumption` CI job exists
precisely to catch these, so expect the occasional CI-only round-trip and do not treat a clean local
build as the final word.

## Releases are separate

Do **not** bump versions in a feature PR. A release is cut after merge by the maintainer via the
`/push-release` flow: tag `vX.Y.Z` on the merged `main` (publishes all 15 packages in lockstep),
then a follow-up FACTS-regen PR and one lockstep version-bump PR per consumer. See
[the published versioning policy](https://ivanball.github.io/docs/guides/common-VERSIONING.html).

## Branch protection (maintainer, run once)

The ruleset lives in GitHub settings, not in the repo. To reproduce it with the CLI (a repo admin,
once), require a PR with the checks above and 0 approvals:

```bash
gh api -X PUT repos/ivanball/MMCA.Common/branches/main/protection \
  --input - <<'JSON'
{
  "required_status_checks": {
    "strict": true,
    "checks": [
      {"context": "build-and-test"},
      {"context": "Build MMCA.Common.UI.Maui (windows, 4 TFMs)"},
      {"context": "UI a11y + render smoke (chromium)"},
      {"context": "UI a11y + render smoke (firefox)"},
      {"context": "UI a11y + render smoke (webkit)"},
      {"context": "coverage"},
      {"context": "Consumer source build (Helpdesk)"},
      {"context": "Performance gate (BenchmarkDotNet Short + baseline verify)"}
    ]
  },
  "enforce_admins": false,
  "required_pull_request_reviews": {"required_approving_review_count": 0},
  "restrictions": null,
  "required_conversation_resolution": true,
  "allow_force_pushes": false,
  "allow_deletions": false
}
JSON
```

All eight contexts above are live today; a context is added here only after its job has a green
streak and is promoted from advisory to required (the path webkit, the Helpdesk canary, and the
performance gate each took). Do not add a `v*` tag protection rule: release tags must keep
triggering `release.yml`.

## License

By contributing you agree that your contributions are licensed under the
[Apache License 2.0](LICENSE), including its patent grant.
