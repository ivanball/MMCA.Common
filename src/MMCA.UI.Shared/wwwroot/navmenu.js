(function () {
    function getToggler() {
        return document.querySelector('.navbar-toggler');
    }

    function closeMenu() {
        var toggler = getToggler();
        if (toggler && toggler.checked) {
            toggler.checked = false;
            toggler.dispatchEvent(new Event('change'));
        }
    }

    // Ensure body overflow matches the actual toggler state.
    // Guards against stale scroll-lock when Blazor navigates or the
    // page is restored from bfcache without triggering a change event.
    function syncOverflow() {
        var toggler = getToggler();
        var shouldLock = toggler && toggler.checked;
        document.body.style.overflow = shouldLock ? 'hidden' : '';
    }

    // Body scroll lock + aria-expanded sync on checkbox change
    document.addEventListener('change', function (e) {
        if (e.target && e.target.classList.contains('navbar-toggler')) {
            document.body.style.overflow = e.target.checked ? 'hidden' : '';
            e.target.setAttribute('aria-expanded', e.target.checked);
        }
    });

    // Close menu when a nav link is clicked (delegated)
    document.addEventListener('click', function (e) {
        if (e.target.closest('.nav-scrollable a, .nav-scrollable .mud-nav-link')) {
            closeMenu();
        }
    });

    // Close menu when backdrop is clicked
    document.addEventListener('click', function (e) {
        if (e.target && e.target.classList.contains('nav-backdrop')) {
            closeMenu();
        }
    });

    // Blazor enhanced navigation replaces content without a full page load.
    // The toggler checkbox may be reset by the DOM diff while the body
    // overflow style remains 'hidden'. Sync on every enhanced navigation.
    document.addEventListener('enhancedload', syncOverflow);

    // Back-forward cache (bfcache) can restore a page with stale overflow.
    window.addEventListener('pageshow', syncOverflow);
})();
