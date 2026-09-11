(function () {
    function getToggler() {
        return document.querySelector('.navbar-toggler');
    }

    function closeMenu(returnFocus) {
        var toggler = getToggler();
        if (toggler && toggler.checked) {
            toggler.checked = false;
            toggler.dispatchEvent(new Event('change', { bubbles: true }));

            // Escape closes a menu the keyboard opened, so focus has to come back to the control
            // that opened it (WCAG 2.4.3): the collapsed menu is visibility:hidden, and focus left
            // inside it would otherwise be dropped onto <body>.
            if (returnFocus) {
                toggler.focus();
            }
        }
        syncOverflow();
    }

    // Keep the announced expanded state on the toggler itself, wherever the state came from.
    function syncExpanded() {
        var toggler = getToggler();
        if (toggler) {
            toggler.setAttribute('aria-expanded', toggler.checked ? 'true' : 'false');
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
            e.target.setAttribute('aria-expanded', e.target.checked ? 'true' : 'false');
        }
    });

    // The toggler renders as role="button", so Space/Enter must work on it the way they do on a
    // button. Space is the checkbox default and already toggles; Enter is not, so drive it here.
    document.addEventListener('keydown', function (e) {
        if (e.key !== 'Enter') {
            return;
        }

        var toggler = getToggler();
        if (toggler && e.target === toggler) {
            e.preventDefault();
            toggler.checked = !toggler.checked;
            toggler.dispatchEvent(new Event('change', { bubbles: true }));
        }
    });

    // Escape closes the mobile menu from anywhere inside it (WCAG 2.1.2: the open menu covers the
    // page, so a keyboard user needs a way out that is not "tab through every link").
    document.addEventListener('keydown', function (e) {
        if (e.key === 'Escape' || e.key === 'Esc') {
            closeMenu(true);
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
    // aria-expanded is re-synced too: it is a plain attribute in the markup, so the diff restores
    // the server-rendered "false" (or leaves a stale "true") independently of the checked state.
    document.addEventListener('enhancedload', function () {
        syncOverflow();
        syncExpanded();
    });

    // Back-forward cache (bfcache) can restore a page with stale overflow.
    window.addEventListener('pageshow', function () {
        syncOverflow();
        syncExpanded();
    });
})();
