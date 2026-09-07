'use strict';

window.jellySpotLog = window.jellySpotLog || {
    info: function (msg) {
        console.log('JellySpot • ' + msg);
    },
    warn: function (msg, detail) {
        if (detail !== undefined) {
            console.warn('JellySpot • ' + msg, detail);
        } else {
            console.warn('JellySpot • ' + msg);
        }
    },
    error: function (msg, detail) {
        if (detail !== undefined) {
            console.error('JellySpot • ' + msg, detail);
        } else {
            console.error('JellySpot • ' + msg);
        }
    }
};

if (typeof window.jellySpotPlugin === 'undefined') {
    const log = window.jellySpotLog;

    window.jellySpotPlugin = {
        TAB_DEFS: {
            browse: { sectionClass: 'jellyspot-browse-sections', defaultTitle: 'Browse' },
            liked: { sectionClass: 'jellyspot-liked-sections', defaultTitle: 'Liked' },
            sync: { sectionClass: 'jellyspot-sync-sections', defaultTitle: 'Sync' },
            queue: { sectionClass: 'jellyspot-queue-sections', defaultTitle: 'Queue' }
        },

        _watchersReady: false,
        _handlersBound: false,
        _tabsEnsuring: false,
        _tabsEnsureQueued: false,
        _tabsEnsureQueueCount: 0,
        _ensureRetryTimers: null,
        _cleanupTimer: null,
        _mounted: {},

        init: function () {
            if (typeof ApiClient === 'undefined') {
                log.warn('init waiting for ApiClient');
                setTimeout(() => this.init(), 200);
                return;
            }

            if (!this._handlersBound) {
                this._handlersBound = true;
                log.info('init binding handlers');
                this.bindMenuInterceptor();
            }

            if (!this._watchersReady) {
                this._watchersReady = true;
                log.info('init setting up native tab watchers');
                this.setupNativeTabWatchers();
            }

            this.ensureDrawerLinks();
            this.scheduleEnsureNativeTabs();
        },

        isHomeHash: function () {
            const hash = String(window.location.hash || '').split('?')[0].replace(/\/+$/, '').toLowerCase();
            return hash === '' ||
                hash === '#' ||
                hash === '#/home' ||
                hash === '#/home.html' ||
                hash === '#home' ||
                hash === '#home.html';
        },

        isHomeViewEvent: function (event) {
            const target = event && event.target;
            if (target && target.nodeType === 1) {
                if (target.id === 'indexPage') {
                    return true;
                }
                if (typeof target.closest === 'function' && target.closest('#indexPage')) {
                    return true;
                }
            }
            return false;
        },

        homePageExists: function () {
            const page = document.getElementById('indexPage');
            return !!(page && (page.querySelector('#homeTab') || page.querySelector('.homeSectionsContainer')));
        },

        isHomeTabContext: function () {
            if (!this.isHomeHash()) {
                return false;
            }

            const page = document.getElementById('indexPage');
            if (!page || page.classList.contains('hide')) {
                return false;
            }

            const visiblePage = document.querySelector('.page:not(.hide)');
            return !visiblePage || visiblePage.id === 'indexPage';
        },

        clearEnsureRetries: function () {
            (this._ensureRetryTimers || []).forEach(function (timer) {
                clearTimeout(timer);
            });
            this._ensureRetryTimers = [];
        },

        scheduleEnsureNativeTabs: function () {
            const self = this;
            self.cancelHeaderCleanup();
            self.clearEnsureRetries();
            [0, 50, 150, 400, 900, 1800, 3500, 6000].forEach(function (delay) {
                const timer = setTimeout(function () {
                    if (!self.isHomeHash()) {
                        return;
                    }
                    self.ensureNativeTabs();
                }, delay);
                self._ensureRetryTimers.push(timer);
            });
        },

        cancelHeaderCleanup: function () {
            if (this._cleanupTimer) {
                clearTimeout(this._cleanupTimer);
                this._cleanupTimer = null;
            }
        },

        scheduleHeaderCleanup: function () {
            const self = this;
            self.cancelHeaderCleanup();
            self._cleanupTimer = setTimeout(function () {
                self._cleanupTimer = null;
                if (!self.isHomeHash()) {
                    self.cleanupHeaderButtons();
                }
            }, 250);
        },

        cleanupHeaderButtons: function () {
            document.querySelectorAll('.headerTabs [data-jellyspot-tab]').forEach(function (btn) {
                btn.remove();
            });
        },

        pick: function (obj) {
            if (!obj) {
                return undefined;
            }
            for (let i = 1; i < arguments.length; i++) {
                if (obj[arguments[i]] !== undefined) {
                    return obj[arguments[i]];
                }
            }
            return undefined;
        },

        escapeHtml: function (text) {
            const div = document.createElement('div');
            div.textContent = text || '';
            return div.innerHTML;
        },

        createTabButton: function (id, title) {
            const titleEl = document.createElement('div');
            titleEl.className = 'emby-button-foreground';
            titleEl.textContent = title || this.TAB_DEFS[id].defaultTitle;

            const button = document.createElement('button');
            button.type = 'button';
            button.setAttribute('is', 'empty-button');
            button.className = 'emby-tab-button emby-button';
            button.setAttribute('data-jellyspot-tab', id);
            button.appendChild(titleEl);
            return button;
        },

        createTabPanel: function (id) {
            const panel = document.createElement('div');
            panel.className = 'tabContent pageTabContent';
            panel.setAttribute('data-jellyspot-tab', id);

            const sections = document.createElement('div');
            sections.className = 'sections ' + this.TAB_DEFS[id].sectionClass;
            panel.appendChild(sections);
            return panel;
        },

        nextTabIndex: function (tabsSlider) {
            let max = -1;
            tabsSlider.querySelectorAll('.emby-tab-button').forEach(function (btn) {
                const n = parseInt(btn.getAttribute('data-index'), 10);
                if (!isNaN(n) && n > max) {
                    max = n;
                }
            });
            return max + 1;
        },

        attachPluginTabGuard: function (tabs) {
            if (!tabs || tabs.dataset.jellyspotPluginTabGuard === 'true') {
                return;
            }

            tabs.dataset.jellyspotPluginTabGuard = 'true';
            log.info('native tab guard attached');
            const self = this;

            tabs.addEventListener('beforetabchange', function (event) {
                if (!self.isHomeTabContext()) {
                    return;
                }

                const index = parseInt(event.detail && event.detail.selectedTabIndex, 10);
                if (isNaN(index)) {
                    return;
                }

                const selectedButton = tabs.querySelector('.emby-tab-button[data-index="' + index + '"]');
                if (selectedButton && selectedButton.getAttribute('data-jellyspot-tab')) {
                    const id = selectedButton.getAttribute('data-jellyspot-tab');
                    setTimeout(function () {
                        if (self.isHomeTabContext()) {
                            self.onTabShown(id);
                        }
                    }, 0);
                }
            }, true);

            tabs.addEventListener('tabchange', function (event) {
                if (!self.isHomeTabContext()) {
                    return;
                }

                const index = parseInt(event.detail && event.detail.selectedTabIndex, 10);
                if (isNaN(index)) {
                    return;
                }

                const selectedButton = tabs.querySelector('.emby-tab-button[data-index="' + index + '"]');
                if (!selectedButton || !selectedButton.getAttribute('data-jellyspot-tab')) {
                    return;
                }

                log.info('blocking Jellyfin tabchange handler for plugin tab index ' + index);
                event.stopImmediatePropagation();
                self.showPluginTab(selectedButton.getAttribute('data-jellyspot-tab'));
            }, true);

            tabs.addEventListener('tabchange', function (event) {
                if (!self.isHomeTabContext()) {
                    return;
                }

                const index = parseInt(event.detail && event.detail.selectedTabIndex, 10);
                const selectedButton = tabs.querySelector('.emby-tab-button[data-index="' + index + '"]');
                if (selectedButton && selectedButton.getAttribute('data-jellyspot-tab')) {
                    return;
                }

                self.hidePluginPanels();
            }, true);
        },

        onTabShown: function (id) {
            this.showPluginTab(id);
        },

        hidePluginPanels: function () {
            const page = document.getElementById('indexPage');
            if (!page) {
                return;
            }
            page.querySelectorAll('.tabContent[data-jellyspot-tab]').forEach(function (panel) {
                panel.classList.add('hide');
                panel.classList.remove('is-active');
            });
            const homeTab = page.querySelector('#homeTab');
            const favoritesTab = page.querySelector('#favoritesTab');
            if (homeTab) {
                homeTab.classList.remove('hide');
            }
            if (favoritesTab) {
                favoritesTab.classList.remove('hide');
            }
        },

        showPluginTab: function (id) {
            const page = document.getElementById('indexPage');
            if (!page || !id) {
                return;
            }
            page.querySelectorAll('.tabContent[data-jellyspot-tab]').forEach(function (panel) {
                const active = panel.getAttribute('data-jellyspot-tab') === id;
                panel.classList.toggle('hide', !active);
                panel.classList.toggle('is-active', active);
            });
            ['#homeTab', '#favoritesTab'].forEach(function (sel) {
                const native = page.querySelector(sel);
                if (native) {
                    native.classList.add('hide');
                    native.classList.remove('is-active');
                }
            });
            this.mountTab(id);
        },

        applyTabs: function (page, tabsSlider) {
            const self = this;
            let changed = false;
            let index = self.nextTabIndex(tabsSlider);

            Object.keys(self.TAB_DEFS).forEach(function (id) {
                let button = tabsSlider.querySelector('[data-jellyspot-tab="' + id + '"]');
                let panel = page.querySelector('.tabContent[data-jellyspot-tab="' + id + '"]');

                if (!button) {
                    button = self.createTabButton(id);
                    tabsSlider.appendChild(button);
                    changed = true;
                }
                if (!panel) {
                    panel = self.createTabPanel(id);
                    page.appendChild(panel);
                    changed = true;
                }

                if (!button.hasAttribute('data-index')) {
                    button.setAttribute('data-index', String(index));
                    panel.setAttribute('data-index', String(index));
                    index += 1;
                    changed = true;
                } else {
                    panel.setAttribute('data-index', button.getAttribute('data-index'));
                }
            });

            return changed;
        },

        ensureNativeTabs: function () {
            const self = this;
            if (self._tabsEnsuring) {
                self._tabsEnsureQueued = true;
                return self._tabsEnsuring;
            }

            if (!self.isHomeHash()) {
                self.scheduleHeaderCleanup();
                return Promise.resolve();
            }

            self.cancelHeaderCleanup();

            self._tabsEnsuring = Promise.resolve().then(function () {
                if (!self.isHomeHash()) {
                    self.scheduleHeaderCleanup();
                    return;
                }

                const page = document.getElementById('indexPage');
                const tabsSlider = document.querySelector('.headerTabs .emby-tabs-slider');
                const tabsEl = document.querySelector('.headerTabs [is="emby-tabs"]');
                if (!page || !tabsSlider || !tabsEl || !self.homePageExists()) {
                    self._tabsEnsureQueued = true;
                    return;
                }

                self.attachPluginTabGuard(tabsEl);
                const changed = self.applyTabs(page, tabsSlider);
                self.ensureDrawerLinks();
                if (changed) {
                    log.info('native tab bar ready: browse, liked, sync, queue');
                    if (typeof tabsEl.refresh === 'function') {
                        try {
                            tabsEl.refresh();
                        } catch (err) {
                            // scroller refresh is best-effort
                        }
                    }
                }
                self.openRequestedTab();
            }).catch(function (err) {
                log.error('failed to ensure native tabs', err);
            }).then(function () {
                self._tabsEnsuring = null;
                if (!self._tabsEnsureQueued) {
                    self._tabsEnsureQueueCount = 0;
                    return;
                }
                self._tabsEnsureQueued = false;
                self._tabsEnsureQueueCount += 1;
                if (self.isHomeHash() && self._tabsEnsureQueueCount < 16) {
                    return self.ensureNativeTabs();
                }
                self._tabsEnsureQueueCount = 0;
            });

            return self._tabsEnsuring;
        },

        requestedTab: function () {
            const hash = String(window.location.hash || '');
            const query = hash.indexOf('?') >= 0 ? hash.slice(hash.indexOf('?') + 1) : '';
            const fromHash = new URLSearchParams(query).get('tab');
            const fromStore = sessionStorage.getItem('jellyspotOpenTab');
            const tab = fromHash || fromStore;
            return tab && this.TAB_DEFS[tab] ? tab : null;
        },

        openRequestedTab: function () {
            const tab = this.requestedTab();
            if (!tab) {
                return;
            }
            const button = document.querySelector('.headerTabs [data-jellyspot-tab="' + tab + '"]');
            if (!button) {
                return;
            }
            sessionStorage.removeItem('jellyspotOpenTab');
            this.showPluginTab(tab);
            if (typeof button.click === 'function') {
                button.click();
            }
        },

        openUserUi: function (tab) {
            const id = this.TAB_DEFS[tab] ? tab : 'browse';
            sessionStorage.setItem('jellyspotOpenTab', id);
            if (this.isHomeHash()) {
                this.ensureNativeTabs().then(() => this.openRequestedTab());
                return;
            }
            window.location.hash = '#/home?tab=' + encodeURIComponent(id);
        },

        isDashboardContext: function () {
            const hash = String(window.location.hash || '').toLowerCase();
            if (hash.indexOf('/dashboard') >= 0) {
                return true;
            }
            const page = document.querySelector('.page:not(.hide)');
            return !!(page && (
                page.classList.contains('type-interior') ||
                page.classList.contains('dashboardDocument')
            ));
        },

        isJellySpotMenuHref: function (href) {
            const value = String(href || '').toLowerCase();
            return value.indexOf('name=jellyspot') >= 0 ||
                (value.indexOf('configurationpage') >= 0 && value.indexOf('jellyspot') >= 0);
        },

        rewriteOfficialMenuLinks: function () {
            const self = this;
            document.querySelectorAll('a[href*="name=JellySpot"], a[href*="name=jellyspot"]').forEach(function (link) {
                if (self.isDashboardContext() && link.closest('.type-interior, .dashboardDocument, #dashboardPage')) {
                    return;
                }
                link.setAttribute('href', '#/home?tab=browse');
                link.setAttribute('data-jellyspot-rewritten', '1');
            });
        },

        bindMenuInterceptor: function () {
            if (this._menuInterceptorBound) {
                return;
            }
            this._menuInterceptorBound = true;
            const self = this;
            document.addEventListener('click', function (event) {
                const link = event.target && event.target.closest
                    ? event.target.closest('a, button')
                    : null;
                if (!link) {
                    return;
                }
                if (link.getAttribute && link.getAttribute('data-jellyspot-tab')) {
                    return;
                }
                const href = link.getAttribute('href') || link.getAttribute('to') || '';
                const shouldOpenUserUi = link.id === 'jellyspot-drawer-browse' ||
                    link.getAttribute('data-jellyspot-rewritten') === '1' ||
                    (self.isJellySpotMenuHref(href) && !self.isDashboardContext());
                if (!shouldOpenUserUi) {
                    return;
                }
                event.preventDefault();
                event.stopPropagation();
                self.openUserUi('browse');
            }, true);
            if (document.body) {
                new MutationObserver(function () {
                    self.ensureDrawerLinks();
                }).observe(document.body, { childList: true, subtree: true });
            }
        },

        setupNativeTabWatchers: function () {
            const self = this;
            log.info('native tab watchers ready');

            document.addEventListener('viewshow', function (event) {
                self.ensureDrawerLinks();
                if (self.isHomeViewEvent(event) || self.isHomeHash()) {
                    self.scheduleEnsureNativeTabs();
                    return;
                }
                self.scheduleHeaderCleanup();
            });

            window.addEventListener('hashchange', function () {
                if (self.isHomeHash()) {
                    self.scheduleEnsureNativeTabs();
                    return;
                }
                self.scheduleHeaderCleanup();
            });
        },

        section: function (id) {
            const page = document.getElementById('indexPage');
            if (!page) {
                return null;
            }
            return page.querySelector('.' + this.TAB_DEFS[id].sectionClass);
        },

        mountTab: function (id) {
            const root = this.section(id);
            if (!root) {
                return;
            }
            if (id === 'browse') {
                this.renderBrowse(root);
            } else if (id === 'liked') {
                this.renderLiked(root);
            } else if (id === 'sync') {
                this.renderSync(root);
            } else if (id === 'queue') {
                this.renderQueue(root);
            }
        },

        renderBrowse: function (root) {
            const self = this;
            if (!root.dataset.jellyspotMounted) {
                root.dataset.jellyspotMounted = 'true';
                root.innerHTML =
                    '<h2 class="sectionTitle">JellySpot Browse</h2>' +
                    '<p class="jellyspot-status">Search Spotify or open your playlists, then queue downloads.</p>' +
                    '<div class="jellyspot-toolbar">' +
                    '<div class="inputContainer" style="flex:1;min-width:220px;margin:0;">' +
                    '<input is="emby-input" type="text" class="jellyspot-search-input" label="Search Spotify" />' +
                    '</div>' +
                    '<button is="emby-button" type="button" class="raised button-submit jellyspot-search-btn"><span>Search</span></button>' +
                    '<button is="emby-button" type="button" class="raised jellyspot-playlists-btn"><span>My Playlists</span></button>' +
                    '<button is="emby-button" type="button" class="raised jellyspot-liked-btn"><span>Liked Songs</span></button>' +
                    '</div>' +
                    '<div class="jellyspot-status jellyspot-browse-status"></div>' +
                    '<div class="jellyspot-grid jellyspot-browse-results"></div>';

                root.querySelector('.jellyspot-search-btn').addEventListener('click', function () {
                    self.searchBrowse(root);
                });
                root.querySelector('.jellyspot-search-input').addEventListener('keydown', function (e) {
                    if (e.key === 'Enter') {
                        e.preventDefault();
                        self.searchBrowse(root);
                    }
                });
                root.querySelector('.jellyspot-playlists-btn').addEventListener('click', function () {
                    self.loadPlaylists(root);
                });
                root.querySelector('.jellyspot-liked-btn').addEventListener('click', function () {
                    self.loadLiked(root);
                });
            }
        },

        setBrowseStatus: function (root, msg) {
            const el = root.querySelector('.jellyspot-browse-status');
            if (el) {
                el.textContent = msg || '';
            }
        },

        coverOf: function (item) {
            return this.pick(item, 'CoverUrl', 'coverUrl', 'ImageUrl', 'imageUrl')
                || (item.album && item.album.images && item.album.images[0] && item.album.images[0].url)
                || (item.images && item.images[0] && item.images[0].url)
                || '';
        },

        addCard: function (grid, title, meta, img, onClick) {
            const card = document.createElement('div');
            card.className = 'jellyspot-card';
            card.innerHTML =
                (img
                    ? '<img src="' + this.escapeHtml(img) + '" alt="" />'
                    : '<div class="jellyspot-card-fallback"></div>') +
                '<strong>' + this.escapeHtml(title) + '</strong>' +
                '<div class="fieldDescription">' + this.escapeHtml(meta) + '</div>';
            const btn = document.createElement('button');
            btn.setAttribute('is', 'emby-button');
            btn.type = 'button';
            btn.className = 'raised button-submit';
            btn.innerHTML = '<span>Download / Monitor</span>';
            btn.addEventListener('click', onClick);
            card.appendChild(btn);
            grid.appendChild(card);
        },

        queueItem: function (kind, id, name, root) {
            const self = this;
            let req;
            if (kind === 'track') {
                req = ApiClient.ajax({
                    type: 'POST',
                    url: ApiClient.getUrl('JellySpot/Queue/Tracks'),
                    data: JSON.stringify({ TrackIds: [id], PlaylistName: 'Manual' }),
                    contentType: 'application/json'
                });
            } else if (kind === 'playlist') {
                req = ApiClient.ajax({
                    type: 'POST',
                    url: ApiClient.getUrl('JellySpot/Queue/Playlist/' + encodeURIComponent(id)),
                    contentType: 'application/json'
                });
            } else if (kind === 'album') {
                req = ApiClient.ajax({
                    type: 'POST',
                    url: ApiClient.getUrl('JellySpot/Queue/Album/' + encodeURIComponent(id)),
                    contentType: 'application/json'
                });
            } else {
                req = ApiClient.ajax({
                    type: 'POST',
                    url: ApiClient.getUrl('JellySpot/Queue/Liked'),
                    contentType: 'application/json'
                });
            }

            req.then(function (res) {
                self.setBrowseStatus(root, 'Queued ' + (self.pick(res, 'Queued', 'queued') != null ? self.pick(res, 'Queued', 'queued') : name));
            }).catch(function () {
                self.setBrowseStatus(root, 'Queue failed — link Spotify under Sync first.');
            });
        },

        searchBrowse: function (root) {
            const self = this;
            const q = (root.querySelector('.jellyspot-search-input').value || '').trim();
            if (!q) {
                return;
            }
            ApiClient.ajax({
                type: 'GET',
                url: ApiClient.getUrl('JellySpot/Search?q=' + encodeURIComponent(q) + '&type=track,album,playlist&limit=10'),
                dataType: 'json'
            }).then(function (data) {
                const grid = root.querySelector('.jellyspot-browse-results');
                grid.innerHTML = '';
                let count = 0;
                function addItems(arr, kind) {
                    (arr || []).forEach(function (item) {
                        count += 1;
                        self.addCard(grid, item.name || 'Untitled', kind, self.coverOf(item), function () {
                            self.queueItem(kind, item.id, item.name, root);
                        });
                    });
                }
                addItems(data.tracks && data.tracks.items, 'track');
                addItems(data.albums && data.albums.items, 'album');
                addItems(data.playlists && data.playlists.items, 'playlist');
                self.setBrowseStatus(root, count + ' results');
            }).catch(function () {
                self.setBrowseStatus(root, 'Search failed. Link Spotify under Sync.');
            });
        },

        artistsOf: function (item) {
            const artists = this.pick(item, 'Artists', 'artists') || [];
            if (Array.isArray(artists)) {
                return artists.join(', ');
            }
            return String(artists);
        },

        loadLiked: function (root) {
            const self = this;
            const grid = root.querySelector('.jellyspot-browse-results') || root.querySelector('.jellyspot-liked-results');
            if (!grid) {
                return;
            }
            ApiClient.ajax({ type: 'GET', url: ApiClient.getUrl('JellySpot/LikedSongs'), dataType: 'json' })
                .then(function (tracks) {
                    grid.innerHTML = '';
                    (tracks || []).forEach(function (t) {
                        const id = self.pick(t, 'Id', 'id');
                        const name = self.pick(t, 'Name', 'name') || 'Untitled';
                        const meta = self.artistsOf(t) || self.pick(t, 'Album', 'album') || 'Liked';
                        self.addCard(grid, name, meta, self.coverOf(t), function () {
                            self.queueItem('track', id, name, root);
                        });
                    });
                    self.setBrowseStatus(root, (tracks || []).length + ' liked songs');
                })
                .catch(function () {
                    self.setBrowseStatus(root, 'Could not load liked songs.');
                });
        },

        renderLiked: function (root) {
            if (!root.dataset.jellyspotMounted) {
                root.dataset.jellyspotMounted = 'true';
                root.innerHTML =
                    '<h2 class="sectionTitle">Liked Songs</h2>' +
                    '<p class="jellyspot-status">Your Spotify liked tracks. Queue one or sync the whole list from Sync.</p>' +
                    '<div class="jellyspot-toolbar">' +
                    '<button is="emby-button" type="button" class="raised button-submit jellyspot-queue-all-liked"><span>Queue all</span></button>' +
                    '</div>' +
                    '<div class="jellyspot-status jellyspot-browse-status"></div>' +
                    '<div class="jellyspot-grid jellyspot-liked-results"></div>';
                const self = this;
                root.querySelector('.jellyspot-queue-all-liked').addEventListener('click', function () {
                    self.queueItem('liked', 'liked', 'Liked Songs', root);
                });
            }
            this.loadLiked(root);
        },

        ensureDrawerLinks: function () {
            this.rewriteOfficialMenuLinks();
            const host = document.querySelector('.mainDrawer-scrollContainer') ||
                document.querySelector('.navDrawer') ||
                document.querySelector('.mainDrawer');
            if (!host || document.getElementById('jellyspot-drawer-browse')) {
                return;
            }

            const self = this;
            const btn = document.createElement('a');
            btn.id = 'jellyspot-drawer-browse';
            btn.className = 'navMenuOption emby-button';
            btn.href = '#/home?tab=browse';
            btn.innerHTML = '<span class="material-icons navMenuOptionIcon music_note" aria-hidden="true"></span><span class="navMenuOptionText">JellySpot</span>';
            btn.addEventListener('click', function (e) {
                e.preventDefault();
                e.stopPropagation();
                self.openUserUi('browse');
            });
            host.appendChild(btn);
        },

        loadPlaylists: function (root) {
            const self = this;
            ApiClient.ajax({ type: 'GET', url: ApiClient.getUrl('JellySpot/Playlists'), dataType: 'json' })
                .then(function (playlists) {
                    const grid = root.querySelector('.jellyspot-browse-results');
                    grid.innerHTML = '';
                    (playlists || []).forEach(function (p) {
                        const id = self.pick(p, 'Id', 'id');
                        const name = self.pick(p, 'Name', 'name') || id;
                        const meta = (self.pick(p, 'TrackCount', 'trackCount') || 0) + ' tracks';
                        self.addCard(grid, name, meta, self.coverOf(p), function () {
                            self.queueItem('playlist', id, name, root);
                        });
                    });
                    self.setBrowseStatus(root, (playlists || []).length + ' playlists');
                })
                .catch(function () {
                    self.setBrowseStatus(root, 'Could not load playlists.');
                });
        },

        renderSync: function (root) {
            const self = this;
            if (!root.dataset.jellyspotMounted) {
                root.dataset.jellyspotMounted = 'true';
                root.innerHTML =
                    '<h2 class="sectionTitle">JellySpot Sync</h2>' +
                    '<p class="jellyspot-status">Link your Spotify account and choose what to keep mirrored.</p>' +
                    '<div class="jellyspot-status jellyspot-link-status">Checking link status…</div>' +
                    '<div class="jellyspot-toolbar">' +
                    '<button is="emby-button" type="button" class="raised button-submit jellyspot-link-btn"><span>Link Spotify</span></button>' +
                    '<button is="emby-button" type="button" class="raised jellyspot-unlink-btn"><span>Unlink</span></button>' +
                    '<button is="emby-button" type="button" class="raised jellyspot-syncnow-btn"><span>Sync now</span></button>' +
                    '</div>' +
                    '<label class="checkboxContainer"><input type="checkbox" class="jellyspot-enabled" checked /> <span>Enable automatic sync for my account</span></label>' +
                    '<label class="checkboxContainer"><input type="checkbox" class="jellyspot-liked" /> <span>Sync Liked Songs</span></label>' +
                    '<div class="inputContainer"><input is="emby-input" type="text" class="jellyspot-include" label="Include artist filters (comma separated)" /></div>' +
                    '<div class="inputContainer"><input is="emby-input" type="text" class="jellyspot-exclude" label="Exclude artist filters (comma separated)" /></div>' +
                    '<h3 class="sectionTitle">Monitored playlists</h3>' +
                    '<div class="jellyspot-monitored"></div>' +
                    '<button is="emby-button" type="button" class="raised button-submit jellyspot-save-btn"><span>Save sync settings</span></button>' +
                    '<div class="jellyspot-status jellyspot-sync-meta"></div>';

                root.querySelector('.jellyspot-link-btn').addEventListener('click', function () {
                    ApiClient.ajax({ type: 'GET', url: ApiClient.getUrl('JellySpot/OAuth/Start'), dataType: 'json' })
                        .then(function (res) {
                            window.open(self.pick(res, 'Url', 'url'), '_blank', 'noopener');
                        })
                        .catch(function () {
                            root.querySelector('.jellyspot-link-status').textContent = 'Could not start OAuth. Set Client ID under Dashboard → JellySpot.';
                        });
                });
                root.querySelector('.jellyspot-unlink-btn').addEventListener('click', function () {
                    ApiClient.ajax({ type: 'POST', url: ApiClient.getUrl('JellySpot/OAuth/Unlink') }).then(function () {
                        self.loadSync(root);
                    });
                });
                root.querySelector('.jellyspot-syncnow-btn').addEventListener('click', function () {
                    ApiClient.ajax({ type: 'POST', url: ApiClient.getUrl('JellySpot/Sync/Now') })
                        .then(function () {
                            root.querySelector('.jellyspot-sync-meta').textContent = 'Sync started. Check Queue.';
                            self.loadSync(root);
                        })
                        .catch(function (err) {
                            root.querySelector('.jellyspot-sync-meta').textContent = (err && err.message) || 'Sync failed';
                        });
                });
                root.querySelector('.jellyspot-save-btn').addEventListener('click', function () {
                    const playlistIds = [];
                    root.querySelectorAll('.jellyspot-monitored input[type=checkbox]').forEach(function (cb) {
                        if (cb.checked) {
                            playlistIds.push(cb.getAttribute('data-playlist-id'));
                        }
                    });
                    const splitCsv = function (value) {
                        return (value || '').split(',').map(function (s) { return s.trim(); }).filter(Boolean);
                    };
                    ApiClient.ajax({
                        type: 'POST',
                        url: ApiClient.getUrl('JellySpot/Settings'),
                        data: JSON.stringify({
                            Enabled: root.querySelector('.jellyspot-enabled').checked,
                            SyncLikedSongs: root.querySelector('.jellyspot-liked').checked,
                            PlaylistIds: playlistIds,
                            IncludeArtistFilters: splitCsv(root.querySelector('.jellyspot-include').value),
                            ExcludeArtistFilters: splitCsv(root.querySelector('.jellyspot-exclude').value)
                        }),
                        contentType: 'application/json'
                    }).then(function () {
                        root.querySelector('.jellyspot-sync-meta').textContent = 'Settings saved';
                        self.loadSync(root);
                    }).catch(function () {
                        root.querySelector('.jellyspot-sync-meta').textContent = 'Failed to save';
                    });
                });
            }
            this.loadSync(root);
        },

        loadSync: function (root) {
            const self = this;
            Promise.all([
                ApiClient.ajax({ type: 'GET', url: ApiClient.getUrl('JellySpot/Me'), dataType: 'json' }),
                ApiClient.ajax({ type: 'GET', url: ApiClient.getUrl('JellySpot/Playlists'), dataType: 'json' }).catch(function () { return []; })
            ]).then(function (results) {
                const me = results[0] || {};
                const playlists = results[1] || [];
                const settings = self.pick(me, 'Settings', 'settings') || {};
                const linked = self.pick(me, 'Linked', 'linked') === true;
                root.querySelector('.jellyspot-link-status').textContent = linked
                    ? ('Linked as ' + (self.pick(me, 'SpotifyDisplayName', 'spotifyDisplayName') || self.pick(me, 'SpotifyUserId', 'spotifyUserId') || 'Spotify user'))
                    : 'Spotify is not linked';
                root.querySelector('.jellyspot-enabled').checked = self.pick(settings, 'Enabled', 'enabled') !== false;
                root.querySelector('.jellyspot-liked').checked = !!self.pick(settings, 'SyncLikedSongs', 'syncLikedSongs');
                root.querySelector('.jellyspot-include').value = (self.pick(settings, 'IncludeArtistFilters', 'includeArtistFilters') || []).join(', ');
                root.querySelector('.jellyspot-exclude').value = (self.pick(settings, 'ExcludeArtistFilters', 'excludeArtistFilters') || []).join(', ');
                root.querySelector('.jellyspot-sync-meta').textContent =
                    'Last sync: ' + (self.pick(settings, 'LastSyncUtc', 'lastSyncUtc') || 'never');

                const box = root.querySelector('.jellyspot-monitored');
                box.innerHTML = '';
                const monitored = self.pick(settings, 'MonitoredPlaylistIds', 'monitoredPlaylistIds') || [];
                playlists.forEach(function (p) {
                    const id = self.pick(p, 'Id', 'id');
                    const name = self.pick(p, 'Name', 'name') || id;
                    const label = document.createElement('label');
                    label.className = 'checkboxContainer';
                    label.style.display = 'block';
                    label.innerHTML = '<input type="checkbox" data-playlist-id="' + self.escapeHtml(id) + '"' +
                        (monitored.indexOf(id) >= 0 ? ' checked' : '') + ' /> <span>' + self.escapeHtml(name) + '</span>';
                    box.appendChild(label);
                });
            }).catch(function (err) {
                root.querySelector('.jellyspot-link-status').textContent =
                    'Unable to load status. Set Spotify Client ID under Dashboard → JellySpot. ' +
                    (err && err.message ? err.message : '');
            });
        },

        renderQueue: function (root) {
            const self = this;
            if (!root.dataset.jellyspotMounted) {
                root.dataset.jellyspotMounted = 'true';
                root.innerHTML =
                    '<h2 class="sectionTitle">JellySpot Queue</h2>' +
                    '<p class="jellyspot-status">Download progress, match scores, and rematch actions.</p>' +
                    '<div class="jellyspot-toolbar">' +
                    '<div class="selectContainer" style="margin:0;"><label class="selectLabel" for="jellyspotQueueFilterHome">Status</label>' +
                    '<select is="emby-select" id="jellyspotQueueFilterHome" class="emby-select-withcolor jellyspot-queue-filter">' +
                    '<option value="">All</option>' +
                    '<option value="Pending">Pending</option>' +
                    '<option value="Matching">Matching</option>' +
                    '<option value="Downloading">Downloading</option>' +
                    '<option value="Completed">Completed</option>' +
                    '<option value="Failed">Failed</option>' +
                    '</select></div>' +
                    '<button is="emby-button" type="button" class="raised jellyspot-queue-refresh"><span>Refresh</span></button>' +
                    '</div>' +
                    '<table class="detailTable jellyspot-queue-table"><thead><tr>' +
                    '<th>Track</th><th>Status</th><th>Score</th><th>Details</th><th></th>' +
                    '</tr></thead><tbody class="jellyspot-queue-body"></tbody></table>';

                root.querySelector('.jellyspot-queue-refresh').addEventListener('click', function () {
                    self.loadQueue(root);
                });
                root.querySelector('.jellyspot-queue-filter').addEventListener('change', function () {
                    self.loadQueue(root);
                });
            }
            this.loadQueue(root);
        },

        loadQueue: function (root) {
            const self = this;
            const status = root.querySelector('.jellyspot-queue-filter').value;
            const url = ApiClient.getUrl('JellySpot/Queue') + (status ? ('?status=' + encodeURIComponent(status)) : '');
            ApiClient.ajax({ type: 'GET', url: url, dataType: 'json' }).then(function (items) {
                const tbody = root.querySelector('.jellyspot-queue-body');
                tbody.innerHTML = '';
                (items || []).forEach(function (item) {
                    const tr = document.createElement('tr');
                    const title = self.pick(item, 'Title', 'title') || '';
                    const artists = self.pick(item, 'Artists', 'artists') || '';
                    const st = self.pick(item, 'Status', 'status') || '';
                    const score = self.pick(item, 'MatchScore', 'matchScore');
                    const detail = self.pick(item, 'Error', 'error') || self.pick(item, 'YoutubeVideoId', 'youtubeVideoId') || '';
                    const id = self.pick(item, 'Id', 'id');
                    tr.innerHTML =
                        '<td><strong>' + self.escapeHtml(title) + '</strong><div class="fieldDescription">' + self.escapeHtml(artists) + '</div></td>' +
                        '<td>' + self.escapeHtml(st) + '</td>' +
                        '<td>' + (score != null ? Number(score).toFixed(1) : '-') + '</td>' +
                        '<td>' + self.escapeHtml(String(detail)) + '</td>' +
                        '<td></td>';
                    if (st === 'Failed' || st === 'Completed') {
                        const btn = document.createElement('button');
                        btn.setAttribute('is', 'emby-button');
                        btn.className = 'raised';
                        btn.type = 'button';
                        btn.innerHTML = '<span>Rematch</span>';
                        btn.addEventListener('click', function () {
                            ApiClient.ajax({
                                type: 'POST',
                                url: ApiClient.getUrl('JellySpot/Queue/' + id + '/Rematch')
                            }).then(function () {
                                self.loadQueue(root);
                            });
                        });
                        tr.children[4].appendChild(btn);
                    }
                    tbody.appendChild(tr);
                });
            }).catch(function () {
                root.querySelector('.jellyspot-queue-body').innerHTML =
                    '<tr><td colspan="5">Could not load queue</td></tr>';
            });
        }
    };

    function boot() {
        log.info('boot');
        window.jellySpotPlugin.init();
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', boot);
    } else {
        boot();
    }

    window.addEventListener('popstate', function () {
        log.info('popstate. restoring tabs');
        if (window.jellySpotPlugin) {
            window.jellySpotPlugin.scheduleEnsureNativeTabs();
        }
        setTimeout(boot, 400);
    });
}

(function () {
    function patch() {
        var icon = document.querySelector('a[href*="name=JellySpot"] .MuiListItemIcon-root');
        if (!icon || icon.dataset.jellyspotMenuIcon) {
            return;
        }
        icon.dataset.jellyspotMenuIcon = '1';
        icon.innerHTML = '<span class="material-icons notranslate MuiIcon-root MuiIcon-fontSizeMedium" aria-hidden="true">music_note</span>';
    }

    if (document.body) {
        new MutationObserver(patch).observe(document.body, { childList: true, subtree: true });
        patch();
    }
})();
