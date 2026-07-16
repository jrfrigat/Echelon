// v1
const CACHE_NAME = 'release-orchestrator-v1';
const offlineResources = ['./', './index.html'];

self.addEventListener('install', event => {
    event.waitUntil(
        caches.open(CACHE_NAME).then(cache => cache.addAll(offlineResources))
    );
});

self.addEventListener('fetch', event => {
    if (event.request.method !== 'GET') return;
    if (event.request.url.includes('/api/')) return;
    event.respondWith(
        caches.match(event.request).then(r => r || fetch(event.request))
    );
});
