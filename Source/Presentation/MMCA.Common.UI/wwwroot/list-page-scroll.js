// Scroll-position helpers for DataGridListPageBase.
// Captures the scrollTop on a debounced scroll listener and pushes it back into Blazor via a
// DotNetObjectReference. Restores via double-RAF so the scroll target lands AFTER MudDataGrid's row
// layout pass on first render.
//
// The scroll target is the document by default. A virtualized grid is height-bound and scrolls inside
// its own viewport instead, so every exported function takes an OPTIONAL trailing containerSelector:
// when supplied AND it matches an element, that element is tracked/restored; otherwise the document
// behavior is unchanged, which keeps the pre-existing call shapes working untouched.

const trackers = new Map();

function getScroller(containerSelector) {
    if (containerSelector) {
        const container = document.querySelector(containerSelector);
        if (container) {
            return container;
        }
    }

    return document.scrollingElement || document.documentElement;
}

export function getScrollPosition(containerSelector) {
    return getScroller(containerSelector).scrollTop || 0;
}

export function setScrollPosition(top, containerSelector) {
    requestAnimationFrame(() => {
        requestAnimationFrame(() => {
            // Resolved inside the double-RAF: a virtualized grid's container may not exist yet when
            // the restore is queued, but it does once the layout pass has run.
            const scroller = getScroller(containerSelector);
            if (scroller === document.scrollingElement || scroller === document.documentElement) {
                scroller.scrollTo(0, top);
            } else {
                scroller.scrollTop = top;
            }
        });
    });
}

export function enableScrollTracking(dotNetRef, id, debounceMs, containerSelector) {
    disableScrollTracking(id);

    const container = containerSelector ? document.querySelector(containerSelector) : null;
    const target = container || window;

    let timeoutId = null;
    const handler = () => {
        if (timeoutId !== null) {
            clearTimeout(timeoutId);
        }
        timeoutId = setTimeout(() => {
            timeoutId = null;
            const top = (container || getScroller()).scrollTop || 0;
            dotNetRef.invokeMethodAsync('OnScrollPositionChanged', top).catch(() => {
                // Circuit may have torn down between scroll and dispatch — ignore.
            });
        }, debounceMs);
    };

    target.addEventListener('scroll', handler, { passive: true });
    trackers.set(id, { handler, target, timeoutId: () => timeoutId });
}

export function disableScrollTracking(id) {
    const entry = trackers.get(id);
    if (entry) {
        (entry.target || window).removeEventListener('scroll', entry.handler);
        trackers.delete(id);
    }
}
