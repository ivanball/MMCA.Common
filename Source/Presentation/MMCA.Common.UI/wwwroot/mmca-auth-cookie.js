// Mirrors the in-memory JWT (supplied as an argument by ISessionCookieSync — never read from
// localStorage) into an HttpOnly cookie on the UI host's origin so SSR prerender of [Authorize] pages
// works after right-click → "Open in new tab" or F5. Invoked via JS interop in Blazor Server and WebAssembly.
window.mmcaAuthCookie = {
    set: async function (accessToken, refreshToken) {
        try {
            await fetch('/auth/session-cookie', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                credentials: 'same-origin',
                body: JSON.stringify({ accessToken: accessToken, refreshToken: refreshToken })
            });
        } catch (e) {
            console.warn('mmcaAuthCookie.set failed', e);
        }
    },
    clear: async function () {
        try {
            await fetch('/auth/session-cookie', {
                method: 'DELETE',
                credentials: 'same-origin'
            });
        } catch (e) {
            console.warn('mmcaAuthCookie.clear failed', e);
        }
    }
};

// Same-origin "validate-or-refresh": returns a currently-valid access token, refreshed server-side from
// the HttpOnly refresh cookie when the access cookie has expired. The refresh token never reaches JS.
// Returns the access token string, or null when there is no valid session.
window.mmcaAuthSession = {
    getToken: async function () {
        try {
            const response = await fetch('/auth/session/token', {
                method: 'POST',
                credentials: 'same-origin',
                headers: { 'Accept': 'application/json' }
            });
            if (!response.ok) {
                return null;
            }
            const data = await response.json();
            return data && data.accessToken ? data.accessToken : null;
        } catch (e) {
            return null;
        }
    }
};
