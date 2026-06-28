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
