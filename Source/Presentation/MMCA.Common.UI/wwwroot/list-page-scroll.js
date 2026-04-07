// Scroll-position helpers for DataGridListPageBase.
// Captures the document's scrollTop on a debounced scroll listener and pushes
// it back into Blazor via a DotNetObjectReference. Restores via double-RAF so
// the scroll target lands AFTER MudDataGrid's row layout pass on first render.

const trackers = new Map();

function getScroller() {
    return document.scrollingElement || document.documentElement;
}

export function getScrollPosition() {
    return getScroller().scrollTop || 0;
}

export function setScrollPosition(top) {
    requestAnimationFrame(() => {
        requestAnimationFrame(() => {
            getScroller().scrollTo(0, top);
        });
    });
}

export function enableScrollTracking(dotNetRef, id, debounceMs) {
    disableScrollTracking(id);

    let timeoutId = null;
    const handler = () => {
        if (timeoutId !== null) {
            clearTimeout(timeoutId);
        }
        timeoutId = setTimeout(() => {
            timeoutId = null;
            const top = getScroller().scrollTop || 0;
            dotNetRef.invokeMethodAsync('OnScrollPositionChanged', top).catch(() => {
                // Circuit may have torn down between scroll and dispatch — ignore.
            });
        }, debounceMs);
    };

    window.addEventListener('scroll', handler, { passive: true });
    trackers.set(id, { handler, timeoutId: () => timeoutId });
}

export function disableScrollTracking(id) {
    const entry = trackers.get(id);
    if (entry) {
        window.removeEventListener('scroll', entry.handler);
        trackers.delete(id);
    }
}
