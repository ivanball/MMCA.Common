// Pre-paint Day/Dark seed for a MAUI Blazor head (ADR-028). Load it as a CLASSIC, synchronous script
// in index.html's <head>, before the stylesheets:
//
//     <script src="_content/MMCA.Common.UI/theme-prepaint.js"></script>
//
// The WebView paints index.html before MudThemeProvider defines --mud-palette-background, and that
// provider's first render still carries the light palette until ThemeService has read the stored mode,
// so a dark-mode user sees a white flash on a cold launch. This stamps <html data-mmca-theme="dark|light">
// before the first paint, reading the persisted preference exactly as theme.js does (localStorage, then
// the mmca_theme cookie) and falling back to the OS scheme the way ThemeService does. The matching
// rules in app.css then paint the host surface in that mode until MmcaThemeProviders marks the page
// interactive (data-mmca-interactive). Not an ES module and no modern syntax on purpose: it must run
// before anything else on the page, on every WebView the MAUI heads target.
(function () {
    var key = 'mmca_theme';
    var theme = null;

    // On a MAUI head's cold launch the NATIVE copy wins: MainPageBase (MMCA.Common.UI.Maui) appends the
    // mode NativeThemeSync saved to device preferences on every change as MmcaTheme/<dark|light>. The
    // WebView flushes localStorage and its cookies lazily, so a relaunch right after a switch still read
    // the previous theme from them and painted it (Run 17 X-27). Only the first page of a WebView session
    // takes the token (sessionStorage marks it): the token is fixed for the session, so a later reload
    // reads the live localStorage value instead, which by then is the newer one.
    try {
        var nativeTheme = /MmcaTheme\/(dark|light)/.exec(navigator.userAgent);
        if (nativeTheme && !sessionStorage.getItem('mmca_theme_prepainted')) {
            theme = nativeTheme[1];
        }
        sessionStorage.setItem('mmca_theme_prepainted', '1');
    } catch (e) {
        theme = null;
    }

    // Then localStorage, then the cookie, in step with theme.js get(): the Android WebView flushes its
    // cookie store lazily, so after a quick kill the cookie can still hold the previous theme.
    if (theme === null) {
        try {
            theme = localStorage.getItem(key);
        } catch (e) {
            theme = null;
        }
    }

    if (theme !== 'dark' && theme !== 'light') {
        theme = null;
        var cookies = document.cookie ? document.cookie.split('; ') : [];
        for (var i = 0; i < cookies.length; i++) {
            if (cookies[i].indexOf(key + '=') === 0) {
                try {
                    theme = decodeURIComponent(cookies[i].substring(key.length + 1));
                } catch (e) {
                    // A malformed cookie value (URIError): fall back to the OS scheme below.
                    theme = null;
                }
            }
        }
    }

    if (theme !== 'dark' && theme !== 'light') {
        theme = window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
    }

    document.documentElement.setAttribute('data-mmca-theme', theme);
})();

// Status bar fallback for an edge-to-edge MAUI head. On Android, MainPageBase (MMCA.Common.UI.Maui)
// appends the native status bar height to the web view's user agent as MmcaStatusBar/<css px>, because
// env(safe-area-inset-top) reads 0 on a cold launch. This exposes it as --mmca-status-bar-fallback
// before the first paint; the head sizes its status bar spacer with
// max(env(safe-area-inset-top, 0px), var(--mmca-status-bar-fallback, 0px)). No token (every other
// platform, and the browser) leaves the property unset, so the inset alone applies. MmcaNavigationBar
// does the same for the bottom inset, as --mmca-navigation-bar-fallback.
(function () {
    var statusBar = /MmcaStatusBar\/(\d+)/.exec(navigator.userAgent);
    if (statusBar) {
        document.documentElement.style.setProperty('--mmca-status-bar-fallback', statusBar[1] + 'px');
    }

    var navigationBar = /MmcaNavigationBar\/(\d+)/.exec(navigator.userAgent);
    if (navigationBar) {
        document.documentElement.style.setProperty('--mmca-navigation-bar-fallback', navigationBar[1] + 'px');
    }
})();
