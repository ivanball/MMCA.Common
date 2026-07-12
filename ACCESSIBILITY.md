# Accessibility (rubric §21)

The shared `MMCA.Common.UI` surface targets **WCAG 2.1 AA**. Accessibility is enforced two ways: an
automated axe-core gate in CI (the bulk of coverage) and a documented manual screen-reader pass (this
file) for the things automation cannot judge (meaningful focus order, sensible reading order, announcement
quality). It complements [ADR-022](ADRs/022-browser-session-cookie-auth.md) (auth UX) and
[ADR-028](ADRs/028-dark-theme-mode.md) (theme).

## Automated coverage (axe-core, WCAG 2.1 AA)

The `ui-e2e` CI job runs Playwright + axe-core against the backend-less gallery; **chromium is the blocking
merge gate** (firefox/webkit advisory). Scanned states:

| Page / state | Test | Notes |
|--------------|------|-------|
| Login | `LoginPageE2ETests.LoginPage_HasNoWcag21AaViolations` | real `MMCA.Common.UI` Login page |
| Register | `RegisterPageE2ETests` | real Register page (EditForm + per-field validation) |
| Components showcase: header, empty state, **loading state**, **error state**, card list, infinite-scroll, delete-confirmation | `ComponentsPageE2ETests.ComponentsPage_HasNoWcag21AaViolations` | broadened 2026-06-29 to include the loading (named progressbar) and error (alert) primitive states |
| **Dark mode**: Login + Components showcase re-scanned with the dark palette active | `DarkModeE2ETests` | gated 2026-07-11 after the dark-palette tuning (dark `PrimaryContrastText`/`ErrorContrastText`); dark mode is seeded via the `mmca_theme` cookie |

Component render is additionally regression-gated in the unit tier by **bUnit render-snapshot tests**
(`PrimitivesSnapshotTests`, rubric §28), so an unintended markup change to a shared primitive fails the
build even without a browser.

### Defect found and fixed by broadening coverage (2026-06-29)

Adding the loading state to the axe scan surfaced a real WCAG 4.1.2 defect in `PageLoadingState`: it wrapped
the spinner in a bare `<div aria-label="...">` (a prohibited ARIA attribute on a non-role element) around an
**anonymous** `progressbar`. Fixed: the wrapper is now `role="status" aria-live="polite"` (a valid live
region) and the spinner carries the accessible name (`aria-label`), so the progressbar is announced.

## Manual screen-reader pass

Automation cannot judge reading order, focus management, or announcement quality, so the shared surface is
walked manually. Checklist (re-run on any change to `MainLayout`, the auth pages, or a shared primitive):

| Check | Surface | Result (2026-06-29) |
|-------|---------|---------------------|
| Skip-to-content link is first in tab order and moves focus to `<main>` | `MainLayout` | PASS (`MainLayout.razor` skip-nav + `role="main"`) |
| Landmarks present and unique (banner / navigation / main) | `MainLayout` | PASS |
| Every interactive control is keyboard-operable and visibly focused | Login / Register / Components | PASS |
| Form fields have programmatic labels; validation errors are tied to the field and announced | Login / Register | PASS (`EditForm` + `ValidationMessage For=`, rubric §24) |
| Icon-only buttons have accessible names (theme toggle, culture switcher, notification bell, delete) | `MainLayout` chrome / Components | PASS (localized `aria-label`s) |
| Status/progress is announced (loading spinner, snackbars) | Components | PASS after the `PageLoadingState` fix above |
| Color is not the only information channel; AA contrast in **light** mode | all | PASS (axe-gated) |

## Known limitations (tracked)

- ~~**Dark-mode contrast (§20, not §21).**~~ **RESOLVED (2026-07-11).** The two dark-palette WCAG AA
  contrast failures the prototype scan flagged (filled-primary button label ~2.65:1 on `#42A5F5`;
  filled error-alert message text ~3.5:1 on `#EF5350`) are fixed by the Material dark-theme treatment:
  `PaletteDark.PrimaryContrastText`/`ErrorContrastText` are now dark (`rgba(0,0,0,0.87)`, ~6.6:1 and
  ~5.5:1), mirroring the existing `WarningContrastText` fix. The dark-mode axe scan is now part of the
  blocking gate (`DarkModeE2ETests`, table above), so the dark palette can no longer regress silently.
- **Per-component-state breadth.** The axe gate covers the gallery's representative states; deep
  consumer-specific states are scored in the consumer apps (ADC/Store).
- **No automated focus-trap / reading-order assertion.** Covered by the manual pass above rather than a
  tool.
