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

// Sets <html lang> to the active UI language (ADR-027 Decision 10). A Blazor Web head emits the right
// value server-side from CurrentCulture, so this is a no-op there. A MAUI Blazor Hybrid head serves a
// STATIC index.html that cannot be templated, so without this the document keeps declaring whatever
// language was hardcoded, misreporting the page language to assistive technology (WCAG 3.1.1) after a
// switch. Automated checks do not catch it: axe flags a missing or malformed lang, never a wrong one.
export function setDocumentLanguage(language) {
    if (language) {
        document.documentElement.lang = language;
    }
}
