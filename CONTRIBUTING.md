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

## License

By contributing you agree that your contributions are licensed under the
[Apache License 2.0](LICENSE), including its patent grant.
