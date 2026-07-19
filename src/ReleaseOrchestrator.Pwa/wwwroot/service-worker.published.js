// Offline cache for the published app.
//
// The cache is keyed on the assets-manifest version, which is derived from the content hashes
// of the published files. A new deploy therefore produces a new cache name: the install step
// re-fetches everything, and activate drops the previous cache. The old hardcoded '-v1' name
// never changed, so index.html was pinned forever and drifted out of sync with blazor.boot.json.
self.importScripts('./service-worker-assets.js');

self.addEventListener('install', event => event.waitUntil(onInstall(event)));
self.addEventListener('activate', event => event.waitUntil(onActivate(event)));
self.addEventListener('fetch', event => event.respondWith(onFetch(event)));

const cacheNamePrefix = 'release-orchestrator-';
const cacheName = `${cacheNamePrefix}${self.assetsManifest.version}`;

// .woff2 is listed explicitly: the icon font is self-hosted (see app.css) and /\.woff$/ does not
// match it, so without this the icons would be the one thing that breaks offline.
const offlineAssetsInclude = [/\.dll$/, /\.pdb$/, /\.wasm/, /\.html/, /\.js$/, /\.json$/, /\.css$/, /\.woff$/, /\.woff2$/, /\.png$/, /\.jpe?g$/, /\.gif$/, /\.ico$/, /\.blat$/, /\.dat$/];
// appsettings.json is deploy-overridable -- the compose override mounts a Local-provider copy over the
// baked (AzureAd) one -- so what is served no longer matches the integrity hash the build recorded.
// Precaching it would fail the SRI check and abort the whole service-worker install. Exclude it (and
// any environment variant); Blazor fetches it fresh from the network at startup, which is what
// environment config should do anyway.
const offlineAssetsExclude = [/^service-worker\.js$/, /(^|\/)appsettings(\.[^/]+)?\.json$/];

async function onInstall() {
    // Everything is precached here from the manifest, with each entry pinned to the integrity
    // hash the build recorded — so there is no need to cache.put() opportunistically on fetch,
    // and no way to cache a response the build did not produce.
    const assetsRequests = self.assetsManifest.assets
        .filter(asset => offlineAssetsInclude.some(pattern => pattern.test(asset.url)))
        .filter(asset => !offlineAssetsExclude.some(pattern => pattern.test(asset.url)))
        .map(asset => new Request(asset.url, { integrity: asset.hash, cache: 'no-cache' }));

    await caches.open(cacheName).then(cache => cache.addAll(assetsRequests));

    // Serve the new build immediately instead of waiting for every tab to close.
    await self.skipWaiting();
}

async function onActivate() {
    const cacheKeys = await caches.keys();
    await Promise.all(cacheKeys
        .filter(key => key.startsWith(cacheNamePrefix) && key !== cacheName)
        .map(key => caches.delete(key)));

    await self.clients.claim();
}

async function onFetch(event) {
    // The API is authenticated and changes constantly; a cached response would be both stale
    // and a way to read another session's data after sign-out.
    if (event.request.method !== 'GET' || new URL(event.request.url).pathname.startsWith('/api/')) {
        return fetch(event.request);
    }

    // Client-side routes have no file behind them, so navigations resolve to the app shell.
    const shouldServeIndexHtml = event.request.mode === 'navigate';
    const request = shouldServeIndexHtml ? 'index.html' : event.request;

    const cache = await caches.open(cacheName);
    const cachedResponse = await cache.match(request);

    return cachedResponse || fetch(event.request);
}
