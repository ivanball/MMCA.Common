// Reads the ASP.NET culture cookie (.AspNetCore.Culture, value "c=<culture>|uic=<uiCulture>") so the
// Blazor WebAssembly client can set its thread culture to match the server's SSR prerender (ADR-027).
// Returns the UI culture string, or null when the cookie is absent/unparseable.
export function getCulture() {
    const cookieName = '.AspNetCore.Culture';
    const cookies = document.cookie ? document.cookie.split('; ') : [];
    for (const cookie of cookies) {
        const separator = cookie.indexOf('=');
        if (separator < 0) {
            continue;
        }
        if (cookie.substring(0, separator) !== cookieName) {
            continue;
        }
        const raw = decodeURIComponent(cookie.substring(separator + 1));
        for (const part of raw.split('|')) {
            if (part.startsWith('uic=')) {
                return part.substring(4);
            }
        }
    }
    return null;
}
