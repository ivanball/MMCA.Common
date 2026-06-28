# ADR-011: Single-Locale by Design (No Internationalization)

## Status
**Superseded by [ADR-027](027-multi-locale-i18n.md) (2026-06-27).** Originally Accepted (2026-06-19).
The "if multi-locale is ever required" scope below is the blueprint ADR-027 implements; this record is
retained for history.

## Context
The MMCA applications (the ADC conference app, the Store) and the `MMCA.Common.UI` library currently ship
a single locale (en-US). The architecture rubric scores Internationalization (§27); rather than leave it
at an implicit zero, this ADR records that single-locale is a **deliberate, scoped decision** — not an
oversight — and states what re-introducing i18n would entail.

## Decision
1. **Single-locale (en-US) is an explicit non-goal for now.** User-facing strings are inline in markup;
   dates/numbers use invariant or fixed formatting where appropriate.
2. **The audience is single-locale.** The Atlanta Developers Conference and the Store target a US/English
   audience; the cost of full i18n (resource extraction, culture-aware formatting, RTL, locale selection)
   is not justified by current demand.
3. **If multi-locale is ever required, it is greenfield work**, scoped as: externalize strings to resource
   files; culture-aware formatting (dates/numbers/currency/time zones); `RequestLocalization` middleware +
   discoverable, persisted locale selection with server/client culture aligned; layout tolerance for text
   expansion (and RTL where required); and pluralization/interpolation via the i18n mechanism rather than
   string concatenation.

## Rationale
- Recording the decision converts an implicit rubric-zero into a conscious, revisitable choice — the same
  posture as the single-region DR acceptance in ADR-009.
- Premature i18n infrastructure adds complexity (resource indirection, culture bugs, layout churn) with no
  current user to benefit.

## Trade-offs
- Adding a locale later touches every view plus the formatting paths — a real but bounded effort, accepted.
- Hard-coded strings make a future extraction larger; mitigated by the component-based UI (strings are
  localized to components/pages, so extraction is mechanical rather than archaeological).
