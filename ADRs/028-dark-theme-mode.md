# ADR-028: Day/Dark Theme Mode

## Status
Accepted (2026-06-27).

## Context
`MMCATheme` (`MMCA.Common.UI/Theme/MMCATheme.cs`) has always defined a complete, brand-tuned `PaletteDark`
alongside `PaletteLight`, but `MudThemeProvider` was hard-wired to light: no `@ref`, no `IsDarkMode`
binding, no toggle, no persistence. Dark mode was designed and then never connected. This ADR connects it.

The mechanics are the same ones ADR-027 solves for locale: a Blazor `InteractiveAuto` app must agree on the
theme across SSR prerender, the InteractiveServer circuit, and the InteractiveWebAssembly client *before*
first paint, or the user sees a flash of the wrong theme (FOUC) on every load. So the theme toggle reuses
the i18n persistence and bootstrap machinery rather than inventing a parallel one.

## Decision

1. **Bind the existing theme.** `MainLayout` binds `MudThemeProvider` with `@ref` + `@bind-IsDarkMode`
   against the already-complete `MMCATheme.Instance`. No new palette work.

2. **A `ThemeService` (`MMCA.Common.UI`) owns the preference**, registered in `AddUIShared`. It holds the
   current mode, reads/writes a **non-HttpOnly cookie + localStorage**, and raises a change event so the
   app-bar toggle and `MainLayout` stay in sync. First-visit default is the OS `prefers-color-scheme`
   (via `MudThemeProvider.GetSystemPreference()`), used only when no cookie/profile value exists.

3. **No flash across SSR → WASM.** The theme cookie is read **server-side during SSR prerender** so the
   first painted HTML already carries the correct theme (a `data-theme` attribute / tiny inline `<head>`
   script set from the cookie before Blazor hydrates); the WASM client reads the same cookie on startup and
   binds `IsDarkMode` to match. This is the cookie-as-single-source-of-truth pattern of ADR-027, applied to
   theme instead of culture.

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
- **Reusing the i18n cookie/profile/bootstrap machinery** means one persistence model and one no-flash
  mechanism for both user preferences, instead of two subtly different ones. Theme and locale are the same
  shape of problem.
- **The palette already existed**, so the cost is wiring + persistence, not design — and `BrandColorTokenTests`
  already guards the C#↔CSS token sync, so the dark surfaces stay on-brand.
- **Defaulting to the OS preference** respects the user's system setting on first visit while letting an
  explicit choice win and follow them across devices.

## Trade-offs
- **The same FOUC hazard as locale** must be handled deliberately at SSR (the `data-theme`/inline-script
  read), or the first paint flashes — there is no free no-flash for InteractiveAuto.
- **Helpdesk's custom layout** had to be touched separately because it does not inherit Common's
  `MainLayout`; future hosts that fork the layout inherit the same obligation.
- **Per-user persistence adds a column** to the Identity `User` (folded into the ADR-027 migration, so no
  extra migration), and a profile-edit surface.

## Related
[ADR-027](027-multi-locale-i18n.md) (shares the cookie source-of-truth, the SSR/WASM no-flash bootstrap,
and the `User` preference migration), [ADR-022](022-browser-session-cookie-auth.md) (the SSR cookie-read
pattern), [ADR-015](015-architecture-fitness-functions.md) (the host-wiring fitness assertion).
```
