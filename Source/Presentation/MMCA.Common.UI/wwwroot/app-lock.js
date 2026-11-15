// Makes the page behind the app-lock overlay inert while the app is locked (BiometricGate).
// MudFocusTrap keeps Tab inside the panel, but a pointer, a screen reader's virtual cursor or a
// programmatic focus can still reach the page underneath; the inert attribute removes every
// element outside the overlay from focus, the accessibility tree and pointer input at once.
//
// Only the siblings of the overlay's ancestor chain are marked, so the overlay itself (and the
// focus trap's own sentinels inside it) stays live. An element that was already inert is left
// alone and is not released later, so this never undoes someone else's inert.

const marked = new Set();

export function inertOutside(lockRoot) {
    release();

    if (!lockRoot || !lockRoot.isConnected) {
        return;
    }

    let node = lockRoot;
    while (node && node !== document.body && node.parentElement) {
        for (const sibling of node.parentElement.children) {
            if (sibling !== node && !sibling.hasAttribute('inert')) {
                sibling.setAttribute('inert', '');
                marked.add(sibling);
            }
        }

        node = node.parentElement;
    }
}

export function release() {
    for (const element of marked) {
        element.removeAttribute('inert');
    }

    marked.clear();
}
