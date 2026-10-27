// MMCA.Common.UI.Web service worker, RETIRED by configuration (Pwa:ServiceWorker:Enabled=false).
// It replaces any earlier worker on the visitor's next navigation, deletes the offline cache, and has
// no fetch handler, so every request goes straight to the network as if no worker were installed.
'use strict';

self.addEventListener('install', () => self.skipWaiting());

self.addEventListener('activate', (event) => {
    event.waitUntil((async () => {
        const keys = await caches.keys();
        await Promise.all(keys
            .filter((key) => key.startsWith('mmca-offline-'))
            .map((key) => caches.delete(key)));

        if (self.registration.navigationPreload) {
            await self.registration.navigationPreload.disable();
        }

        await self.clients.claim();
    })());
});
