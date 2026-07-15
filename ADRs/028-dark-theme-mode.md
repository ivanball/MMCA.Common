# ADR-028: Day/Dark Theme Mode

## Status
Accepted (2026-06-27; revised 2026-07-15).

## Context
`MMCATheme` (`MMCA.Common.UI/Theme/MMCATheme.cs`) has always defined a complete, brand-tuned `PaletteDark`
alongside `PaletteLight`, but `MudThemeProvider` was hard-wired to light: no `@ref`, no `IsDarkMode`
binding, no toggle, no persistence. Dark mode was designed and then never connected. This ADR connects it.

The mechanics are the same ones ADR-027 solves for locale: a Blazor `InteractiveAuto` app must agree on the
theme across SSR prerender, the InteractiveServer circuit, and the InteractiveWebAssembly client to avoid a
flash of the wrong theme (FOUC) on load. So the theme toggle reuses the i18n persistence machinery (cookie +
localStorage + profile) rather than inventing a parallel one; the matching no-flash SSR bootstrap is the
intended end state but is not yet wired for theme (see Decision 3).

## Decision

1. **Bind the existing theme.** The shared `MainLayout` renders a single `<MmcaThemeProviders />`
   component (`MMCA.Common.UI/Layout/MainLayout.razor:14`), which owns the four Mud providers plus the
   Day/Dark lifecycle in one place. Inside that component `MudThemeProvider` is bound with
   `@bind-IsDarkMode` against the already-complete `MMCATheme.Instance`
   (`MMCA.Common.UI/Components/MmcaThemeProviders.razor:11`), a two-way binding to that component's own
   `_isDarkMode` field (`MmcaThemeProviders.razor:17`); no `@ref` is used. The layout no longer holds the
   provider markup or the `_isDarkMode` field itself. No new palette work.

2. **A `ThemeService` (`MMCA.Common.UI`) owns the preference**, registered in `AddUIShared`. It holds the
   current mode, reads/writes a **non-HttpOnly cookie + localStorage**, and raises a change event so the
   app-bar toggle and `MainLayout` stay in sync. First-visit default is the OS `prefers-color-scheme`, read
   via a small JS interop call (`theme.js` `systemPrefersDark()` →
   `window.matchMedia('(prefers-color-scheme: dark)')`), used only when no cookie/profile value exists.

3. **Theme is restored from the cookie/localStorage after first render — the no-flash SSR bootstrap is
   outstanding.** `ThemeService.InitializeAsync` reads the persisted value via JS interop from
   `OnAfterRenderAsync(firstRender)` and deliberately does **not** run during SSR prerender, so the bound
   `IsDarkMode` is corrected just after hydration. The cookie-as-single-source-of-truth persistence of
   ADR-027 is reused, but the *server-side* prerender read that makes locale flash-free (a `data-theme`
   attribute / inline `<head>` script emitted from the cookie before Blazor hydrates) is **not yet wired for
   theme**. A brief wrong-theme flash on first paint is therefore currently possible; emitting the theme
   server-side at prerender to close it is tracked as follow-up.

4. **The toggle ships in the shared `MainLayout`**, next to the i18n culture switcher, in the app-bar
   `appbar-icon-actions` slot — so every consumer gets both controls without per-host wiring.

5. **The choice is persisted to the Identity profile (`User.PreferredTheme`)**, in the *same* migration and
   with the *same* login-reconciliation rule as `User.PreferredCulture` (ADR-027): DB is the cross-device
   source of truth, the cookie is the runtime channel; on login the cookie is set from the profile, an
   authenticated toggle writes both, anonymous users get cookie/localStorage only.

6. **Helpdesk is brought into line.** Its host's custom `MainLayout` used a bare `<MudThemeProvider />`
   (not even `MMCATheme`); it is aligned to `MMCATheme.Instance` + the bound `IsDarkMode` + the toggle. As
   an `InteractiveServer`-only host it has no WASM boundary, but it still reads the cookie for consistency.

## Rationale
- **Reusing the i18n cookie/profile machinery** means one persistence model for both user preferences,
  instead of two subtly different ones. Theme and locale are the same shape of problem, so the no-flash SSR
  bootstrap built for locale is the template theme will follow when it is wired.
- **The palette already existed**, so the cost is wiring + persistence, not design — and `BrandColorTokenTests`
  already guards the C#↔CSS token sync, so the dark surfaces stay on-brand.
- **Defaulting to the OS preference** respects the user's system setting on first visit while letting an
  explicit choice win and follow them across devices.

## Trade-offs
- **The same FOUC hazard as locale is not yet closed for theme.** The SSR `data-theme`/inline-script read is
  unimplemented (Decision 3), so the first paint can briefly flash the wrong theme before the post-render JS
  interop corrects it; there is no free no-flash for InteractiveAuto.
- **Helpdesk's custom layout** had to be touched separately because it does not inherit Common's
  `MainLayout`; future hosts that fork the layout inherit the same obligation.
- **Per-user persistence adds a column** to the Identity `User` (folded into the ADR-027 migration, so no
  extra migration), and a profile-edit surface.

## Related
[ADR-027](027-multi-locale-i18n.md) (shares the cookie source-of-truth and the `User` preference migration,
and is the model for the theme no-flash SSR bootstrap that is not yet wired),
[ADR-022](022-browser-session-cookie-auth.md) (the SSR cookie-read pattern),
[ADR-015](015-architecture-fitness-functions.md) (the host-wiring fitness assertion).
```
