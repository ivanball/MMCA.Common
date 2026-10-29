// MMCA.Common.UI.Web service worker, served by MapCommonServiceWorker() at /service-worker.js.
// It makes the host installable and shows an offline page when a navigation cannot reach the
// network. It never caches pages or data: a server-rendered page carries the signed-in user's data,
// so every navigation goes to the network. Only GET navigations are handled; every other request
// (API calls, the Blazor circuit, hubs, framework files) returns from the fetch handler untouched
// and takes the browser's default network path. The only cached response is the offline page.
'use strict';

const VERSION = '__MMCA_SW_VERSION__';
const CACHE_PREFIX = 'mmca-offline-';
const CACHE = CACHE_PREFIX + VERSION;
const OFFLINE_URL = '/_content/MMCA.Common.UI.Web/offline.html';

self.addEventListener('install', (event) => {
    event.waitUntil(
        caches.open(CACHE)
            .then((cache) => cache.add(new Request(OFFLINE_URL, { cache: 'reload' })))
            .then(() => self.skipWaiting()));
});

self.addEventListener('activate', (event) => {
    event.waitUntil((async () => {
        const keys = await caches.keys();
        await Promise.all(keys
            .filter((key) => key.startsWith(CACHE_PREFIX) && key !== CACHE)
            .map((key) => caches.delete(key)));

        // Navigation preload starts the network request while the worker boots, so routing
        // navigations through the worker adds no latency to the page.
        if (self.registration.navigationPreload) {
            await self.registration.navigationPreload.enable();
        }

        await self.clients.claim();
    })());
});

self.addEventListener('fetch', (event) => {
    const request = event.request;
    if (request.method !== 'GET' || request.mode !== 'navigate') {
        return;
    }

    event.respondWith((async () => {
        try {
            const preloaded = await event.preloadResponse;
            return preloaded || await fetch(request);
        } catch {
            const cache = await caches.open(CACHE);
            return (await cache.match(OFFLINE_URL)) || Response.error();
        }
    })());
});
