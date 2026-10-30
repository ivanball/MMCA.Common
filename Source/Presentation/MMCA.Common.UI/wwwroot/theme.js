// Persists and reads the Day/Dark theme preference (ADR-028). The value ("dark"/"light") is stored in a
// non-HttpOnly cookie, because this module writes and reads it from script (no server-side code reads
// it, so the theme is applied once the interactive runtime calls get()), and mirrored to localStorage.
const KEY = 'mmca_theme';

// localStorage is read first, the cookie second (theme-prepaint.js reads in the same order). set()
// writes both, but an Android WebView flushes its cookie store only every ~30 s, so a MAUI app killed
// soon after a toggle relaunched with the PREVIOUS theme from the cookie while localStorage held the
// new one (Store local test run 2, X-30).
export function get() {
    try {
        const stored = localStorage.getItem(KEY);
        if (stored === 'dark' || stored === 'light') {
            return stored;
        }
    } catch {
        // localStorage may be unavailable (private mode); the cookie is the fallback.
    }

    const cookies = document.cookie ? document.cookie.split('; ') : [];
    for (const cookie of cookies) {
        const separator = cookie.indexOf('=');
        if (separator < 0) {
            continue;
        }
        if (cookie.substring(0, separator) === KEY) {
            try {
                return decodeURIComponent(cookie.substring(separator + 1));
            } catch {
                // A malformed cookie value (URIError): set() rewrites the cookie on the next toggle.
                return null;
            }
        }
    }

    return null;
}

export function set(value) {
    const oneYearSeconds = 60 * 60 * 24 * 365;
    document.cookie = `${KEY}=${encodeURIComponent(value)}; path=/; max-age=${oneYearSeconds}; samesite=lax`;
    try {
        localStorage.setItem(KEY, value);
    } catch {
        // localStorage may be unavailable (private mode); get() then falls back to the cookie.
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

// ── Colour-mode hook (opt-in, Layout:StampColorModeOnDocument) ──────────────────────────────
// Mirrors the Day/Dark mode onto the document element as data-mmca-color-mode="light|dark", so an
// app's own stylesheet can select on it. Deliberately not data-mmca-theme: that attribute belongs to
// the MAUI head's pre-paint script and carries pre-paint rules in app.css. Enhanced navigation
// resynchronises <html>'s attributes from the new document, so the last mode is re-stamped on
// 'enhancedload', as the interactivity marker is.
const COLOR_MODE_ATTRIBUTE = 'data-mmca-color-mode';

let lastColorMode = null;
let colorModeReStampAttached = false;

function applyColorMode() {
    if (lastColorMode === null) {
        return;
    }
    try {
        document.documentElement.setAttribute(COLOR_MODE_ATTRIBUTE, lastColorMode);
    } catch {
        // Best-effort: an app's styling hook never surfaces into theme handling.
    }
}

export function stampColorMode(mode) {
    lastColorMode = mode === 'dark' ? 'dark' : 'light';
    applyColorMode();

    if (colorModeReStampAttached) {
        return;
    }

    try {
        document.addEventListener('enhancedload', applyColorMode);
        colorModeReStampAttached = true;
    } catch {
        // No enhanced navigation on this host; the stamp above is enough.
    }
}