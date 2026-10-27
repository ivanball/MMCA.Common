// Registers the framework service worker (MapCommonServiceWorker, /service-worker.js) once the page
// has loaded, so registration never competes with first paint. A separate file because the host's
// Content-Security-Policy blocks inline script.
(() => {
    if (!('serviceWorker' in navigator) || !window.isSecureContext) {
        return;
    }

    const register = () => navigator.serviceWorker
        .register('/service-worker.js', { scope: '/', updateViaCache: 'none' })
        .catch((error) => console.warn('Service worker registration failed.', error));

    if (document.readyState === 'complete') {
        register();
    } else {
        window.addEventListener('load', register, { once: true });
    }
})();
