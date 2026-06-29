# ADR-027: Multi-Locale Internationalization (Supersedes ADR-011)

## Status
Accepted (2026-06-27). **Supersedes [ADR-011](011-single-locale-i18n.md)** (single-locale by design).

## Context
ADR-011 recorded single-locale (en-US) as a deliberate, *revisitable* non-goal and sketched what
re-introducing i18n would entail. That revisit has now happened: the framework adds first-class
internationalization so consumers can serve en-US and Spanish (`es`), with the structure to add more
locales later. ADR-011's own "if multi-locale is ever required" scope is the blueprint this ADR
implements; ADR-011 is now superseded, not deleted (the history matters).

The hard part is not translation files — it is making one culture decision flow consistently through a
Blazor `InteractiveAuto` app (SSR prerender → InteractiveServer circuit → InteractiveWebAssembly client)
*and* through the cross-origin REST services behind the Gateway, without a flash of the wrong language or
a prerender/hydration mismatch. The Result pattern (ADR-013) already gives every `Error` a stable
machine `Code`, which makes server-side error localization a keyed lookup rather than a rewrite.

## Decision

1. **Supported cultures are an explicit allowlist: `en-US` (default) + `es`.** Adding a locale is adding a
   `.es.resx` sibling set and one allowlist entry, not new infrastructure.

2. **Strings are externalized to `.resx`, co-located with the type that uses them, looked up by
   `IStringLocalizer<T>`.** `AddLocalization()` is registered with **no `ResourcesPath`** so a type's
   resource base name is its full type name and the `.resx` lives next to it (`Login.razor` →
   `Login.resx` / `Login.es.resx`; a `*.Resources.SharedResource` marker for cross-cutting chrome). Keys
   are dotted and stable (`Nav.Home`, `Common.Button.Save`). Parameterized text uses **composite format
   keys** (`"Error loading {0}. {1}"`) consumed as `L["Common.Error.Load", entity, detail]` — never string
   concatenation. The `.resx` compile to **satellite assemblies** that pack into the NuGet packages
   automatically (no `.csproj` change) and flow identically via `local.props` source mode.

3. **Backend user-facing error text is localized server-side at the HTTP edge, keyed by `Error.Code`.**
   `IErrorLocalizer` (`MMCA.Common.API/Localization`) maps an error's stable `Code` to a localized string
   against `CurrentUICulture`, falling back to the error's existing English `Message` when no resource key
   exists. It is applied at the single Result→ProblemDetails projection point
   (`ErrorHttpMapping.BuildErrorsExtension`, used by `ApiControllerBase.HandleFailure` and
   `UnhandledResultFailureFilter`). **Domain, handler, and `Result` signatures do not change** — they stay
   culture-agnostic; only the edge speaks a culture. Modules register their own resource sources
   (`ErrorResourceSource`) additively; Common registers its own in `AddAPI`. FluentValidation rules carry
   stable `.WithErrorCode("<Area>.<Field>.<Rule>")` codes so validation errors localize through the same
   mechanism.

4. **The ProblemDetails `title` is a machine marker and is never localized.** The UI error parser
   (`ServiceExceptionHelper`) branches on `title` (`"Operation failed"` / `"Domain Exception"` /
   `"Validation Exception"`); only the human-facing `message`/`detail` is translated.

5. **One culture cookie is the single source of truth across SSR + Server + WASM.** UI hosts run
   `UseRequestLocalization([en-US, es])` with a `CookieRequestCultureProvider` so SSR prerender renders in
   the right culture; a `/culture/set` endpoint writes the standard ASP.NET culture cookie and forces a
   full reload; the WASM client reads the same cookie on startup (`MmcaCultureBootstrap.SetBrowserCultureAsync`) and sets
   `CultureInfo.DefaultThreadCurrent[UI]Culture` before `RunAsync()`, so prerender and hydration agree.
   The UI forwards the active culture to the API as `Accept-Language` (`CultureDelegatingHandler` on the
   `"APIClient"`), because the cross-origin Gateway does not carry the cookie to the services — that header
   is what makes backend errors come back localized.

6. **A user's chosen culture is persisted to the Identity profile (`User.PreferredCulture`).** The DB value
   is the cross-device source of truth; the cookie is the runtime channel. On login the cookie is set from
   the profile; an authenticated switch persists to both DB and cookie; anonymous users get the cookie only.

7. **Display formatting is culture-aware; machine boundaries stay invariant.** UI rendering of dates /
   numbers uses `CurrentCulture`. `InvariantCulture` is retained where the string is a machine contract
   (JWT timestamps, EF/grid filter parsing, URL/query state, claims, value-object canonical strings).
   Hygiene against accidental culture-less formatting is **currently advisory, not a build gate**: the
   Meziantou analyzer `MA0076` (implicit culture-sensitive `ToString` in interpolation) is set to
   `suggestion` severity in `.editorconfig`, *not* an ADR-015 NetArchTest fitness rule. Promoting it to a
   gate (raise `MA0076` to `error`, or add a culture-hygiene rule to the fitness library) is tracked as
   follow-up.

## Rationale
- **Keying error localization on the existing `Error.Code` is the cheapest correct seam.** The codes are
  already stable and already cross the wire; localizing at the edge keeps the Result pattern pure and means
  an untranslated code degrades gracefully to its English message instead of throwing.
- **A single cookie avoids the InteractiveAuto split-brain.** SSR and WASM run in different runtimes; the
  only state both can read before first paint is a non-HttpOnly cookie, so it is the source of truth.
- **Co-located `.resx` with no `ResourcesPath`** makes the resource base name predictable (the full type
  name) and packs cleanly through the lockstep NuGet pipeline (ADR-016) without per-project MSBuild tweaks.

## Trade-offs
- **Every view and every user-facing message is touched** — a large, mostly mechanical sweep, accepted as
  the cost ADR-011 always named.
- **WASM Spanish formatting needs ICU globalization data** (not `InvariantGlobalization`), a payload cost
  on the client bundle.
- **Mixed-language responses are possible during rollout** — an untranslated code falls back to English by
  design, so coverage is incremental rather than all-or-nothing within a release.
- **MudBlazor's own built-in component text** may need a `MudLocalizer` for full coverage; tracked as a
  follow-up rather than blocking.

## Related
[ADR-011](011-single-locale-i18n.md) (superseded), [ADR-013](013-result-pattern.md) (the `Error.Code`
this localizes on), [ADR-015](015-architecture-fitness-functions.md) (where an i18n-hygiene gate would live; today only the
advisory `MA0076` analyzer suggestion exists),
[ADR-016](016-lockstep-versioning-masstransit-pin.md) (satellite assemblies ship in the lockstep release),
[ADR-022](022-browser-session-cookie-auth.md) (the SSR cookie pattern this mirrors),
[ADR-028](028-dark-theme-mode.md) (the theme toggle that shares this cookie/profile/bootstrap machinery).
```
