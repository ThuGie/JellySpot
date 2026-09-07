'use strict';

(function () {
    if (window.__jellySpotNavInstalled) {
        return;
    }
    window.__jellySpotNavInstalled = true;

    function openJellySpot() {
        // Same route jellyfin-web uses for EnableInMainMenu plugin pages
        var href = 'configurationpage?name=JellySpot';
        if (window.Dashboard && typeof Dashboard.navigate === 'function') {
            Dashboard.navigate(href);
            return;
        }
        window.location.hash = '#!/' + href;
    }

    function ensureButton() {
        if (document.getElementById('jellyspot-nav-btn')) {
            return;
        }

        var btn = document.createElement('button');
        btn.id = 'jellyspot-nav-btn';
        btn.type = 'button';
        btn.setAttribute('title', 'JellySpot');
        btn.className = 'headerButton headerButtonRight paper-icon-button-light';
        btn.innerHTML = '<span class="material-icons music_note" aria-hidden="true"></span>';
        btn.addEventListener('click', function (e) {
            e.preventDefault();
            openJellySpot();
        });

        var headerRight = document.querySelector('.headerRight') ||
            document.querySelector('.skinHeader .headerTop .headerRight') ||
            document.querySelector('.skinHeader');
        if (headerRight) {
            headerRight.insertBefore(btn, headerRight.firstChild);
        }
    }

    function tick() {
        try {
            ensureButton();
        } catch (e) {
            // ignore until DOM is ready
        }
    }

    setInterval(tick, 2000);
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', tick);
    } else {
        tick();
    }
})();
