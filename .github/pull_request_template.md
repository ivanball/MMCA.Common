<!-- MMCA.Common pull request. Keep the summary short; the checklist is the important part. -->

## Summary

<!-- What changed and why. Link the ADR / scorecard category / issue if there is one. -->

## Scope of change

- [ ] No public API change (internal only)
- [ ] Public API change (new/changed types, members, or signatures) - **breaking for consumers?** describe the impact below
- [ ] New or changed cross-cutting pattern (an ADR is read/updated below)

## Consumer impact (fill in if the public API changed)

<!-- A change to a public type flows to MMCA.ADC / MMCA.Store / MMCA.Helpdesk only after a
     release + lockstep sweep (ADR-016). If this is a breaking change, say what consumers
     must change. The Helpdesk source-build canary job validates the framework against a real
     consumer inside this PR. -->

## Checklist

- [ ] `dotnet build MMCA.Common.slnx -c Release` is clean (`TreatWarningsAsErrors` on, 5 analyzers at error).
- [ ] `dotnet test --solution MMCA.Common.slnx -c Release` passes (domain + architecture run headless; SQL-dependent tests run in CI).
- [ ] If a project reference or a type moved between packages, both layer gates were updated (the compile-time `LayerEnforcement.targets` guard and the NetArchTest fitness rules).
- [ ] `FACTS.md` regenerated if this changes the version, package list, ADR range, or fitness counts: `dotnet run --project build/facts -- .` (CI gates on `--check` drift).
- [ ] The relevant ADR (published at https://ivanball.github.io/docs/adr/, source in the Website repo) was read, and updated/added there if a pattern it describes changed.
- [ ] The published scorecard/backlog (https://ivanball.github.io/docs/governance/, source in the Website repo) updated if this closes or moves a remediation item.
- [ ] Commit messages follow Scoped Commits (`<scope>: <description>`; `§<m>:` for scorecard work, `ADR-NNN:` for ADR work). See CONTRIBUTING.md.
- [ ] No secrets staged (`.pem`, `.env`, `credentials`, `local.props`).

<!-- Releases are cut separately, after merge, via the /push-release flow (tag -> publish 15 packages -> sweep consumers). Do not bump versions in this PR. -->
