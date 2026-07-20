# ADR-049: Library-Scoped ConfigureAwait(false) Policy (CA2007)

## Status
Accepted (2026-07-20).

## Context
MMCA.Common ships as NuGet packages consumed by host applications, not as an application itself.
Library code that awaits without `ConfigureAwait(false)` captures the caller's `SynchronizationContext`
and resumes on it. In ASP.NET Core hosts there is no synchronization context, so the capture is a
no-op; that is why the workspace baseline disables the ConfigureAwait analyzers everywhere
(`CA2007`, `MA0004`, `RCS1090`, `VSTHRD111` in each repo's `.editorconfig`), and for the three
application repos (Store, ADC, Helpdesk) that remains the right call.

But the framework's packages do not get to choose their callers. `MMCA.Common.UI.Maui` (ADR-042)
runs inside MAUI, which HAS a UI synchronization context, and any future non-ASP.NET consumer
(WPF/WinForms tooling, a console host with a custom context) inherits the same exposure: a
context-capturing await inside the packages is the classic library deadlock and needless
context-hopping cost. Until now the framework relied on the ASP.NET-only assumption instead of the
standard .NET library guidance (libraries call `ConfigureAwait(false)`; applications do not need to).

## Decision
Packaged non-UI framework code awaits with `ConfigureAwait(false)`; UI component packages and
application code do not.

- **Enforcement is a build gate, not a convention.** The MMCA.Common `.editorconfig` repo-delta
  section raises `CA2007` to `warning` for `[Source/**.cs]` (a build error under
  `TreatWarningsAsErrors`), scoped back to `none` for `[Source/Presentation/MMCA.Common.UI*/**.cs]`.
  Tests keep the baseline (xUnit has no synchronization context worth preserving, and test code is
  not shipped).
- **UI component packages are excluded deliberately.** `MMCA.Common.UI` and `MMCA.Common.UI.Maui`
  contain Blazor components and MAUI capability adapters whose continuations must resume on the
  renderer/UI context; `ConfigureAwait(false)` there would be a bug, not hygiene.
- **The application repos keep the baseline.** Store, ADC and Helpdesk are ASP.NET Core hosts
  (plus Blazor/MAUI heads); `CA2007`/`MA0004` stay off in the shared analyzer baseline, per the
  same guidance that libraries and applications have opposite defaults.
- **One analyzer owns the rule.** `CA2007` is the enforced gate; the overlapping `MA0004`,
  `RCS1090` and `VSTHRD111` stay disabled so a violation reports once, not four times.

## Rationale
- **Correctness for the one consumer that already has a context.** The MAUI head consumes
  Infrastructure/Application/API packages through DI; a sync-over-async call anywhere in that stack
  (or a consumer's `.GetAwaiter().GetResult()` bridge) deadlocks only when the library captured the
  context. `ConfigureAwait(false)` removes the failure mode at the source.
- **Standard .NET library guidance, applied at the boundary where it holds.** The rule is scoped to
  exactly the code that ships in packages; it is not blanket-applied to the apps, where it would be
  360+ sites of pure noise (measured across Store/ADC before this decision).
- **Mechanical and self-maintaining.** The fix is `dotnet format analyzers --diagnostics CA2007`;
  the build gate keeps new awaits compliant without review effort.

## Trade-offs
- **Visual noise in framework source.** Every await in `Source/` (except UI packages) carries
  `.ConfigureAwait(false)` (324 sites at adoption). The gate makes it uniform, so the noise is
  consistent rather than sporadic.
- **A per-repo delta in an otherwise shared analyzer baseline.** The workspace keeps one
  byte-identical `.editorconfig` baseline across the four repos; this policy lives in the marked
  repo-delta section of MMCA.Common's file and is verified by the workspace drift script
  (`Tools\Scripts\compare-analyzer-config.ps1`), so the divergence is documented and guarded.
- **UI exclusion relies on project naming.** The `MMCA.Common.UI*` path glob is what exempts the
  component packages; a renamed or relocated UI project would silently fall under the gate (the
  build would fail loudly on the first missing `ConfigureAwait`, so the failure is visible, just
  not self-explaining).

## Related
ADR-042 (the MAUI package whose synchronization context motivates the policy), ADR-027 (the same
"machine-boundary hygiene as a build gate" posture applied to culture-explicit formatting via
MA0076), ADR-015 (fitness-function philosophy: invariants enforced by the build, not by review).
