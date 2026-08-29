// Persists and reads the Day/Dark theme preference (ADR-028). The value ("dark"/"light") is stored in a
// non-HttpOnly cookie (so SSR can read it for a no-flash first paint) and mirrored to localStorage.
const KEY = 'mmca_theme';

export function get() {
    const cookies = document.cookie ? document.cookie.split('; ') : [];
    for (const cookie of cookies) {
        const separator = cookie.indexOf('=');
        if (separator < 0) {
            continue;
        }
        if (cookie.substring(0, separator) === KEY) {
            return decodeURIComponent(cookie.substring(separator + 1));
        }
    }
    try {
        return localStorage.getItem(KEY);
    } catch {
        return null;
    }
}

export function set(value) {
    const oneYearSeconds = 60 * 60 * 24 * 365;
    document.cookie = `${KEY}=${encodeURIComponent(value)}; path=/; max-age=${oneYearSeconds}; samesite=lax`;
    try {
        localStorage.setItem(KEY, value);
    } catch {
        // localStorage may be unavailable (private mode); the cookie is the source of truth.
    }
}

export function systemPrefersDark() {
    return !!(window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches);
}

// ── E2E interactivity marker ────────────────────────────────────────────────
// Stamps the document element with the marker that MMCA.Common.Testing.E2E's WaitForBlazorAsync waits
// on. It lives here (rather than in its own asset) because MmcaThemeProviders already imports this
// module on its first interactive render, so the function is loaded wherever the marker can be set,
// with no extra script tag for a consumer to remember. Keep the attribute name in step with
// PageExtensions.InteractiveMarkerPredicate.
const INTERACTIVE_ATTRIBUTE = 'data-mmca-interactive';

let reStampAttached = false;

function stampInteractive() {
    try {
        document.documentElement.setAttribute(INTERACTIVE_ATTRIBUTE, 'true');
    } catch {
        // Best-effort test infrastructure: never surface into theme initialisation.
    }
}

export function markInteractive() {
    stampInteractive();

    if (reStampAttached) {
        return;
    }

    try {
        // Blazor enhanced navigation synchronises <html>'s attributes with the newly loaded document,
        // which drops the marker even though the runtime stays interactive. Blazor raises 'enhancedload'
        // after each of those updates, so re-stamp there instead of leaving the E2E gate to fall back to
        // its slower legacy settle for the rest of the session.
        document.addEventListener('enhancedload', stampInteractive);
        reStampAttached = true;
    } catch {
        // No enhanced navigation on this host; the initial stamp is enough.
    }
}
