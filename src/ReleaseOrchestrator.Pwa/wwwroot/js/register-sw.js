// Registration lives in a file rather than an inline <script> because the API serves
// script-src 'self' 'wasm-unsafe-eval' - an inline block would be blocked outright.
//
// Development builds get the no-op wwwroot/service-worker.js; publishing swaps in
// service-worker.published.js, which is the one that actually caches.
if ('serviceWorker' in navigator) {
    window.addEventListener('load', () => {
        navigator.serviceWorker.register('service-worker.js')
            .catch(err => console.error('Service worker registration failed:', err));
    });
}
