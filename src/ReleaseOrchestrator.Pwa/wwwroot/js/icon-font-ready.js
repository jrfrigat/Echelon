// Flare renders icons as Material Symbols ligatures (<span class="material-symbols-rounded">home</span>),
// so on a cold load the raw ligature NAMES would paint as text until the font arrives. app.css hides
// them until this adds the ready class, so they appear as glyphs instead of flashing their names.
// visibility:hidden reserves layout, so nothing shifts.
//
// This lives in a file rather than an inline <script> because the API serves
// script-src 'self' 'wasm-unsafe-eval' - an inline block would be blocked outright.
(function () {
    var reveal = function () { document.documentElement.classList.add('flare-icons-ready'); };
    try {
        if (document.fonts && document.fonts.load) {
            document.fonts.load('24px "Material Symbols Rounded"').then(reveal, reveal);
            document.fonts.ready.then(reveal);
        } else {
            reveal();
        }
    } catch (e) {
        reveal();
    }
    // Safety: never leave icons hidden if the font fails to load.
    setTimeout(reveal, 3000);
})();
