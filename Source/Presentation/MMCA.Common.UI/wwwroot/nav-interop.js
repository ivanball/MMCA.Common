// Centralised navigation/session interop helpers used by NavigationHistoryService,
// ListPageStateService (sessionStorage tier), and MauiBackNavigationBridge.
//
// Every call is wrapped in try/catch so that:
//   * Safari Private Browsing (which throws on storage writes)
//   * iframes / WebViews with disabled storage
//   * SSR-time invocations that race the JS runtime
// degrade gracefully and never break the calling Blazor component.

// ── sessionStorage helpers ──────────────────────────────────────────────────

export function sessionGet(key) {
    try {
        const raw = window.sessionStorage.getItem(key);
        if (raw === null || raw === undefined) {
            return null;
        }
        return JSON.parse(raw);
    } catch {
        return null;
    }
}

export function sessionSet(key, value) {
    try {
        window.sessionStorage.setItem(key, JSON.stringify(value));
        return true;
    } catch {
        return false;
    }
}

export function sessionRemove(key) {
    try {
        window.sessionStorage.removeItem(key);
        return true;
    } catch {
        return false;
    }
}

// Removes every key that starts with the prefix and nothing else (sign-out forgets the
// list pages' mmca.lps:* state). Keys are collected first because removing while
// walking sessionStorage by index shifts the indices. Returns how many were removed.
export function sessionRemoveByPrefix(prefix) {
    try {
        const doomed = [];
        for (let i = 0; i < window.sessionStorage.length; i++) {
            const key = window.sessionStorage.key(i);
            if (key !== null && key.startsWith(prefix)) {
                doomed.push(key);
            }
        }
        for (const key of doomed) {
            window.sessionStorage.removeItem(key);
        }
        return doomed.length;
    } catch {
        return 0;
    }
}

// ── History helpers ─────────────────────────────────────────────────────────

export function historyLength() {
    try {
        return window.history.length || 0;
    } catch {
        return 0;
    }
}

export function historyBack() {
    try {
        window.history.back();
        return true;
    } catch {
        return false;
    }
}

// Used by MauiBackNavigationBridge: attempt to go back inside the WebView's
// own history stack. Returns { handled, atRoot } so the MAUI host can decide
// whether to bubble the back gesture (e.g. exit the app on Android root).
export function tryGoBack() {
    try {
        const len = window.history.length || 0;
        // history.length includes the current entry, so > 1 means there is a previous entry.
        if (len > 1) {
            window.history.back();
            return { handled: true, atRoot: false };
        }
        return { handled: false, atRoot: true };
    } catch {
        return { handled: false, atRoot: true };
    }
}
