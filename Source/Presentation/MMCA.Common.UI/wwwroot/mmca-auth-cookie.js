// Mirrors the JWT stored in localStorage into an HttpOnly cookie on the UI host's origin
// so SSR prerender of [Authorize] pages works after right-click → "Open in new tab" or F5.
// Invoked by ISessionCookieSync via JS interop in both Blazor Server and WebAssembly.
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
