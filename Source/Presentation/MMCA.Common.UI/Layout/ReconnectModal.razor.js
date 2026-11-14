// Set up event handlers
const reconnectModal = document.getElementById("components-reconnect-modal");
reconnectModal.addEventListener("components-reconnect-state-changed", handleReconnectStateChanged);

const retryButton = document.getElementById("components-reconnect-button");
retryButton.addEventListener("click", retry);

const resumeButton = document.getElementById("components-resume-button");
resumeButton.addEventListener("click", resume);

function handleReconnectStateChanged(event) {
    if (event.detail.state === "show") {
        reconnectModal.showModal();
    } else if (event.detail.state === "hide") {
        reconnectModal.close();
    } else if (event.detail.state === "failed") {
        document.addEventListener("visibilitychange", retryWhenDocumentBecomesVisible);
        focusActionButton(retryButton);
    } else if (event.detail.state === "paused") {
        focusActionButton(resumeButton);
    } else if (event.detail.state === "rejected") {
        location.reload();
    }
}

// The dialog opens on "show" with nothing focusable in it; the moment it starts offering an action,
// focus has to land on that action so a keyboard user can reach it without hunting (WCAG 2.4.3).
// requestAnimationFrame lets the state class (and so the button's display:block) land first:
// focus() on a display:none element is a silent no-op.
function focusActionButton(button) {
    requestAnimationFrame(function () {
        if (button && button.getClientRects().length > 0) {
            button.focus();
        }
    });
}

async function retry() {
    document.removeEventListener("visibilitychange", retryWhenDocumentBecomesVisible);

    try {
        // Reconnect will asynchronously return:
        // - true to mean success
        // - false to mean we reached the server, but it rejected the connection (e.g., unknown circuit ID)
        // - exception to mean we didn't reach the server (this can be sync or async)
        const successful = await Blazor.reconnect();

        // A true that leaves the dialog open is not a rejoin either: once the server has refused
        // the circuit on a connection that stayed up, reconnect() answers true without rejoining
        // anything (the "hide" state only arrives when the circuit really comes back). In both
        // cases we reached the server but the circuit is no longer available, so resume it or
        // reload the page so the user can continue as quickly as possible.
        if (!successful || reconnectModal.open) {
            await resumeOrReload();
        }
    } catch (err) {
        if (isRefusedByServer(err)) {
            // The server answered and refused the rejoin (for example "because user is
            // unauthorized"). Retrying would ask the same question again, so resume or reload.
            await resumeOrReload();
        } else {
            // We got an exception, server is currently unavailable
            document.addEventListener("visibilitychange", retryWhenDocumentBecomesVisible);
        }
    }
}

// A server that refused a hub invocation answers with an error naming it ("Failed to invoke
// 'ConnectCircuit' because user is unauthorized"); a transport failure (server unreachable) never does.
function isRefusedByServer(err) {
    const message = err && err.message ? String(err.message) : String(err ?? "");
    return /Failed to invoke|unauthorized/i.test(message);
}

async function resumeOrReload() {
    try {
        if (await Blazor.resumeCircuit()) {
            reconnectModal.close();
            return;
        }
    } catch {
        // A refused or failed resume leaves a reload as the way back.
    }

    location.reload();
}

async function resume() {
    try {
        const successful = await Blazor.resumeCircuit();
        if (!successful) {
            location.reload();
        }
    } catch {
        location.reload();
    }
}

async function retryWhenDocumentBecomesVisible() {
    if (document.visibilityState === "visible") {
        await retry();
    }
}
