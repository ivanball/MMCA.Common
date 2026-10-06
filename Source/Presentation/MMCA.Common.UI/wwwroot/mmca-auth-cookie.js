// Mirrors the in-memory JWT (supplied as an argument by ISessionCookieSync — never read from
// localStorage) into an HttpOnly cookie on the UI host's origin so SSR prerender of [Authorize] pages
// works after right-click → "Open in new tab" or F5. Invoked via JS interop in Blazor Server and WebAssembly.
// Both helpers return whether the endpoint answered 2xx, so the caller can tell a written cookie from
// a failed one (a 429 from the host limiter, a dropped connection). The clear is tried twice.
window.mmcaAuthCookie = {
    set: async function (accessToken, refreshToken) {
        try {
            const response = await fetch('/auth/session-cookie', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                credentials: 'same-origin',
                body: JSON.stringify({ accessToken: accessToken, refreshToken: refreshToken })
            });
            return response.ok;
        } catch (e) {
            console.warn('mmcaAuthCookie.set failed', e);
            return false;
        }
    },
    clear: async function () {
        let ok = false;
        for (let attempt = 0; attempt < 2 && !ok; attempt++) {
            try {
                const response = await fetch('/auth/session-cookie', {
                    method: 'DELETE',
                    credentials: 'same-origin'
                });
                ok = response.ok;
            } catch (e) {
                console.warn('mmcaAuthCookie.clear failed', e);
                ok = false;
            }
        }
        return ok;
    }
};

// Same-origin "validate-or-refresh": returns a currently-valid access token, refreshed server-side from
// the HttpOnly refresh cookie when the access cookie has expired. The refresh token never reaches JS.
// Returns the access token string, or null ONLY when the endpoint answers 401 (no valid session). Any
// other failure (a 429 from the host limiter, a 5xx, a dropped connection) throws, because it says
// nothing about the session and the caller must not treat it as "signed out".
window.mmcaAuthSession = {
    getToken: async function () {
        const response = await fetch('/auth/session/token', {
            method: 'POST',
            credentials: 'same-origin',
            headers: { 'Accept': 'application/json' }
        });
        if (response.status === 401) {
            return null;
        }
        if (!response.ok) {
            throw new Error('mmcaAuthSession.getToken: HTTP ' + response.status);
        }
        const data = await response.json();
        return data && data.accessToken ? data.accessToken : null;
    }
};

// Same-origin API proxy hosts (AddCommonSameOriginApiProxy, MMCA.Common.UI.Web). The Blazor Server
// circuit holds its tokens in server memory and must never pass them through this page, so these two
// helpers only ever carry an opaque, server-encrypted, short-lived handoff string: JS can neither read
// a token out of it nor use it against any API. Both send the proxy's fixed CSRF header.
window.mmcaAuthHandoff = {
    // Seeds the HttpOnly session cookies from a protected token pair the circuit produced.
    setCookie: async function (handoff) {
        try {
            const response = await fetch('/auth/session-cookie/handoff', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json', 'X-CSRF': '1' },
                credentials: 'same-origin',
                body: JSON.stringify({ handoff: handoff })
            });
            return response.ok;
        } catch (e) {
            console.warn('mmcaAuthHandoff.setCookie failed', e);
            return false;
        }
    },
    // Validate-or-refresh from the session cookies; returns a protected access token for the circuit,
    // or null ONLY when the endpoint answers 401 (no valid session). Any other failure throws, for the
    // same reason as mmcaAuthSession.getToken.
    getToken: async function () {
        const response = await fetch('/auth/session/handoff', {
            method: 'POST',
            credentials: 'same-origin',
            headers: { 'Accept': 'application/json', 'X-CSRF': '1' }
        });
        if (response.status === 401) {
            return null;
        }
        if (!response.ok) {
            throw new Error('mmcaAuthHandoff.getToken: HTTP ' + response.status);
        }
        const data = await response.json();
        return data && data.handoff ? data.handoff : null;
    }
};
