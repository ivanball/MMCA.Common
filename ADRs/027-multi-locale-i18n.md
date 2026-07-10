# ADR-027: Multi-Locale Internationalization (Supersedes ADR-011)

## Status
Accepted (2026-06-27, amended 2026-07-02, 2026-07-03, and 2026-07-09). **Supersedes [ADR-011](011-single-locale-i18n.md)** (single-locale by design).

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
   Hygiene against accidental culture-less formatting is **enforced as a build gate** (since 2026-06-29):
   the Meziantou analyzer `MA0076` (implicit culture-sensitive `ToString` in interpolation) is set to
   `error` severity in `.editorconfig`, so a culture-less interpolation fails the build and must declare an
   explicit `IFormatProvider` (`CultureInfo.InvariantCulture` at machine boundaries, `CurrentCulture` for
   UI display). This closes the prior "advisory only" follow-up.

8. **Translation completeness is a fitness gate (ADR-015).** `ResourceTranslationsAreComplete`
   (`MMCA.Common.Testing.Architecture`, run as `LocalizationResourceTests` against `SupportedCultures.All`)
   fails the build if any base `.resx` under `Source/` lacks a complete, non-empty sibling for a required
   culture — so a new English string cannot ship without its Spanish translation. Coverage is **verified,
   not assumed**, closing the prior "no missing-key/translation-coverage gate" follow-up. The rule is opt-in
   and repo-agnostic (it takes the required-culture list), so the consumer apps can adopt the same gate for
   their module `.resx`.

   **Locale-addition governance.** Adding a locale is a bounded, gated process: (a) add the culture to
   `SupportedCultures.All`; (b) add the `.<culture>.resx` sibling for every base `.resx`; (c) the coverage
   fitness gate then refuses to build until every key is translated. No other infrastructure change is
   needed: `UseRequestLocalization`, the culture switcher, and the Identity `User.PreferredCulture` guard
   all read `SupportedCultures`, so they cannot drift from the allowlist.

   **Development-only pseudo-localization.** A Windows-standard pseudo-locale, `qps-Ploc`
   (`SupportedCultures.PseudoLocale`), is available as a developer diagnostic and is deliberately kept out of
   `SupportedCultures.All` so the coverage gate never demands a `.qps-Ploc.resx` sibling. It is offered only
   when the host runs in Development: `UseCommonRequestLocalization` adds it to the request-localization
   allowlist under `IsDevelopment()`, and `MapCultureEndpoint` honors it from the culture switcher only under
   the same guard. When it is the active UI culture, a `PseudoStringLocalizerFactory` decorator (registered
   unconditionally, inert under every other culture) runtime-transforms every resolved resource string
   (accents, padding, and a bracket sentinel) so that hard-coded strings, truncation, and string
   concatenation become visible without translating anything. Outside Development it is never offered and the
   decorator stays inert, so it is a build-and-test aid, not a production culture.

   **The pseudo pass is also a required CI gate (since 2026-07-03).** The backend-less gallery host
   (test-only, never packaged) enables `qps-Ploc` unconditionally, and `PseudoLocalizationE2ETests`
   (in the required chromium `ui-e2e` job) renders `/login`, `/register`, and `/components` under it,
   asserting (a) the bracket sentinel appears (every displayed string made the resource round-trip)
   and (b) the page does not overflow horizontally under the ~40% expansion (the layout-tolerance
   criterion). A leak-guard test asserts the sentinel is absent under `en-US`. Production hosts are
   unchanged: they keep `qps-Ploc` Development-only.

9. **User-visible literals are kept out of markup and code-behind by a second fitness gate, and
   composed sentences are banned.** `LocalizedTextConventionTestsBase`
   (`MMCA.Common.Testing.Architecture`, subclassed by every repo) scans `Source/**/*.razor{,.cs}` and
   fails the build on hard-coded snackbar messages, page `Title` properties, literal `<PageTitle>`
   markup, literal breadcrumb labels, and `NavItem` rows that carry no `TitleResource`; deliberate
   literals (brand names) are exempted per line with an `i18n: allow` marker. Snackbar text uses
   **whole-sentence keys in the page's own resource pair** (`Snackbar.Created` = "Event created
   successfully." / "Evento creado correctamente."): `ErrorMessages.Success(entity, action)` is
   `[Obsolete]` because fragment composition cannot translate (Spanish gender agreement breaks), and
   the shared `Common.Error.Load/Save/Delete` templates no longer append raw `ex.Message` (neither
   localizable nor safe to surface).

   **Carve-out (2026-07-09): a `DomainInvariantViolationException` message IS shown verbatim.**
   `ErrorMessages.LoadError/SaveError/DeleteError` return that exception's `Message` in place of the
   generic template, and the new `ErrorMessages.ActionError(ex, localizedFallback)` does the same for
   pages whose fallback is a whole-sentence snackbar key. This does not weaken the raw-text rule:
   `ServiceExceptionHelper` mints that exception type exclusively from the API's Problem Details
   errors, whose text is curated domain wording already localized server-side to the request culture
   (Decision 3, carried by the Decision 5 `Accept-Language` forwarding), so the user sees the actual
   business rule ("This action is only available while the event is live.") instead of a generic
   failure toast. All other exception types keep the generic localized message.

   `NavItem` gained an optional `TitleResource` type: when set, the
   shared `NavMenu` treats `Title`/`Group` as resource keys resolved per circuit at render time, so
   module nav menus follow the active culture. MudBlazor's own component chrome localizes through
   `ResxMudLocalizer` over the `MudTranslations` resource pair (all built-in keys of the pinned
   MudBlazor version, en + es), registered in `AddUIShared` and covered by the same completeness gate.

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
- **Mixed-language responses are possible during rollout**: an untranslated code falls back to English by
  design, so coverage is incremental rather than all-or-nothing within a release.
- **MudBlazor's own built-in component text** may need a `MudLocalizer` for full coverage; tracked as a
  follow-up rather than blocking. **Closed 2026-07-03:** `ResxMudLocalizer` + the `MudTranslations`
  resource pair now localize the MudBlazor chrome (Decision 9); unknown keys still fall back to
  MudBlazor's built-in English, and `en-US` deliberately keeps the built-ins.

## Related
[ADR-011](011-single-locale-i18n.md) (superseded), [ADR-013](013-result-pattern.md) (the `Error.Code`
this localizes on), [ADR-015](015-architecture-fitness-functions.md) (the i18n gates now live here: the `MA0076` culture-less
formatting build gate and the `ResourceTranslationsAreComplete` translation-coverage fitness rule),
[ADR-016](016-lockstep-versioning-masstransit-pin.md) (satellite assemblies ship in the lockstep release),
[ADR-022](022-browser-session-cookie-auth.md) (the SSR cookie pattern this mirrors),
[ADR-028](028-dark-theme-mode.md) (the theme toggle that shares this cookie/profile/bootstrap machinery).
```
