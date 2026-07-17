# Contributing to MMCA.Common

Thanks for taking an interest in the framework. This is a short guide; the full contributor
reference (package layout, layer rules, build/test commands) is [CLAUDE.md](CLAUDE.md).

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
  (see [ArchitectureScorecard.md](ArchitectureScorecard.md) for the categories).
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
  Read the relevant one in [ADRs/README.md](ADRs/README.md) before changing a pattern it
  describes; substantive pattern changes should update or add an ADR.

## Pull request workflow

`main` is protected. All changes land through a pull request; nobody pushes to `main` directly.

1. Branch from an up-to-date `main` (e.g. `feature/<short-name>` or `fix/<short-name>`).
2. Commit your work (Scoped Commits, above), push the branch, and open a PR against `main`.
3. CI runs automatically. The required merge gates are:
   - `build-and-test` (includes the FACTS drift gate, the vuln audit, and the tests)
   - `Build MMCA.Common.UI.Maui (windows, 4 TFMs)`
   - `UI a11y + render smoke (chromium)` and `UI a11y + render smoke (firefox)`
   - `coverage` (the unit-tier line-coverage floor)
   - `Consumer source build (Helpdesk)` - the cross-repo canary (see below)

   `UI a11y + render smoke (webkit)` is advisory (not required). The automated Claude review
   also comments on every PR.
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
- **Local source mode** lets you iterate a consumer against your Common branch with no token:
  in a consumer repo, `cp local.props.template local.props` (it sets `UseLocalMMCA=true` and points
  at `../MMCA.Common/Source/`). Rebuild Common in Debug first (`dotnet build <Common proj> -c Debug`)
  before rebuilding the consumer, or the IDE binds a stale ref assembly and reports phantom `CS0103`.
  A green local source-mode build is not proof CI package-mode is green; expect a CI round-trip.

## Releases are separate

Do **not** bump versions in a feature PR. A release is cut after merge by the maintainer via the
`/push-release` flow: tag `vX.Y.Z` on the merged `main` (publishes all 15 packages in lockstep),
then a follow-up FACTS-regen PR and one lockstep version-bump PR per consumer. See
[VERSIONING.md](VERSIONING.md).

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
      {"context": "coverage"}
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

Add `{"context": "Consumer source build (Helpdesk)"}` to the checks list once that canary job has a
green streak and is promoted from advisory to required. Do not add a `v*` tag protection rule:
release tags must keep triggering `release.yml`.

## License

By contributing you agree that your contributions are licensed under the
[Apache License 2.0](LICENSE), including its patent grant.
