// Centralised navigation/session interop helpers used by NavigationHistoryService,
// ListPageStateService (sessionStorage tier), MauiBackNavigationBridge, and PageStateScope.
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

// ── bfcache handler ─────────────────────────────────────────────────────────
// Registers a single pageshow listener that fires the supplied .NET callback
// whenever the page is restored from the back-forward cache. Useful for forcing
// a state refresh on pages that may otherwise show stale content after browser
// back navigation.

let bfcacheHandler = null;
let bfcacheDotNetRef = null;

export function registerBfcacheHandler(dotNetRef) {
    unregisterBfcacheHandler();

    bfcacheDotNetRef = dotNetRef;
    bfcacheHandler = (event) => {
        try {
            const navEntries = performance.getEntriesByType('navigation');
            const navType = navEntries.length > 0 ? navEntries[0].type : '';
            const restored = event.persisted === true || navType === 'back_forward';
            if (!restored) {
                return;
            }
            dotNetRef.invokeMethodAsync('OnPageRestoredAsync').catch(() => {
                // Component disposed or circuit torn down — ignore.
            });
        } catch {
            // Defensive: never let a bfcache handler throw into the host.
        }
    };

    try {
        window.addEventListener('pageshow', bfcacheHandler);
    } catch {
        bfcacheHandler = null;
        bfcacheDotNetRef = null;
    }
}

export function unregisterBfcacheHandler() {
    if (bfcacheHandler !== null) {
        try {
            window.removeEventListener('pageshow', bfcacheHandler);
        } catch {
            // Best-effort cleanup.
        }
    }
    bfcacheHandler = null;
    bfcacheDotNetRef = null;
}
