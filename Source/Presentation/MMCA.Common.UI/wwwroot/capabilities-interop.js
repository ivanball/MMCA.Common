// Browser implementations of the device-capability contracts (Services/Capabilities):
// share, clipboard, external links, aria-live announcements, online/offline watching,
// and localStorage-backed device preferences / offline cache.
//
// Every call is wrapped in try/catch so that Safari Private Browsing, permission-denied
// clipboard/share calls, iframes with disabled storage, and SSR-time invocations that
// race the JS runtime all degrade gracefully (return false/null) and never break the
// calling Blazor component. Mirrors the conventions of nav-interop.js.

// ── Share / clipboard / external links ──────────────────────────────────────

export async function shareLink(title, url) {
    try {
        if (!navigator.share) {
            return false;
        }
        await navigator.share({ title: title, url: url });
        return true;
    } catch {
        // AbortError (user dismissed) or NotAllowedError — treat as "not shared".
        return false;
    }
}

export async function copyText(text) {
    try {
        if (!navigator.clipboard) {
            return false;
        }
        await navigator.clipboard.writeText(text);
        return true;
    } catch {
        return false;
    }
}

export function openExternal(url) {
    try {
        window.open(url, '_blank', 'noopener,noreferrer');
        return true;
    } catch {
        return false;
    }
}

// ── aria-live screen-reader announcements ───────────────────────────────────
// A single visually-hidden polite live region, created on first use. Styles are
// inline so the region works without any stylesheet dependency.

let liveRegion = null;

function ensureLiveRegion() {
    if (liveRegion !== null && document.body.contains(liveRegion)) {
        return liveRegion;
    }
    const region = document.createElement('div');
    region.setAttribute('aria-live', 'polite');
    region.setAttribute('role', 'status');
    region.style.position = 'absolute';
    region.style.width = '1px';
    region.style.height = '1px';
    region.style.margin = '-1px';
    region.style.padding = '0';
    region.style.overflow = 'hidden';
    region.style.clipPath = 'inset(50%)';
    region.style.whiteSpace = 'nowrap';
    region.style.border = '0';
    document.body.appendChild(region);
    liveRegion = region;
    return region;
}

export function announce(message) {
    try {
        const region = ensureLiveRegion();
        // Clear first so repeating the same message is re-announced.
        region.textContent = '';
        window.setTimeout(() => {
            region.textContent = message;
        }, 50);
        return true;
    } catch {
        return false;
    }
}

// ── online/offline watching ─────────────────────────────────────────────────

let onlineHandler = null;
let offlineHandler = null;

export function watchOnline(dotNetRef) {
    unwatchOnline();
    try {
        const notify = () => {
            dotNetRef.invokeMethodAsync('OnBrowserConnectivityChanged', navigator.onLine === true).catch(() => {
                // Component disposed or circuit torn down — ignore.
            });
        };
        onlineHandler = notify;
        offlineHandler = notify;
        window.addEventListener('online', onlineHandler);
        window.addEventListener('offline', offlineHandler);
        return navigator.onLine === true;
    } catch {
        onlineHandler = null;
        offlineHandler = null;
        return true;
    }
}

export function unwatchOnline() {
    try {
        if (onlineHandler !== null) {
            window.removeEventListener('online', onlineHandler);
        }
        if (offlineHandler !== null) {
            window.removeEventListener('offline', offlineHandler);
        }
    } catch {
        // Best-effort cleanup.
    }
    onlineHandler = null;
    offlineHandler = null;
}

// ── localStorage helpers (device preferences + offline cache) ───────────────
// Values arrive already JSON-serialized from C#; store them as raw strings.

export function storageGet(key) {
    try {
        return window.localStorage.getItem(key);
    } catch {
        return null;
    }
}

export function storageSet(key, value) {
    try {
        window.localStorage.setItem(key, value);
        return true;
    } catch {
        return false;
    }
}

export function storageRemove(key) {
    try {
        window.localStorage.removeItem(key);
        return true;
    } catch {
        return false;
    }
}
