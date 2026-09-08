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

            this.patchForeignTabManagers();
            this.scheduleEnsureNativeTabs();
            this.watchHeaderTabs();
        },

        patchForeignTabManagers: function () {
            const self = this;
            function wrap(obj) {
                if (!obj || typeof obj.removeUnplannedTabButtons !== 'function' ||
                    obj.removeUnplannedTabButtons._jellyspotWrapped) {
                    return !!obj && obj.removeUnplannedTabButtons && obj.removeUnplannedTabButtons._jellyspotWrapped;
                }
                const original = obj.removeUnplannedTabButtons;
                const wrapped = function (tabsSlider, plannedButtons) {
                    if (tabsSlider && Array.isArray(plannedButtons)) {
                        Array.from(tabsSlider.querySelectorAll('[data-jellyspot-tab]')).forEach(function (btn) {
                            if (plannedButtons.indexOf(btn) < 0) {
                                plannedButtons.push(btn);
                            }
                        });
                    }
                    const result = original.apply(this, arguments);
                    setTimeout(function () {
                        self.ensureNativeTabs();
                    }, 50);
                    return result;
                };
                wrapped._jellyspotWrapped = true;
                obj.removeUnplannedTabButtons = wrapped;
                log.info('JellySeerr will keep JellySpot Home tabs');
                return true;
            }

            if (wrap(window.jellySeerrPlugin) || this._foreignPatchTimer) {
                return;
            }
            let tries = 0;
            this._foreignPatchTimer = setInterval(function () {
                tries += 1;
                if (wrap(window.jellySeerrPlugin) || tries > 40) {
                    clearInterval(self._foreignPatchTimer);
                    self._foreignPatchTimer = null;
                }
            }, 250);
        },

        isHomeHash: function () {
            const hash = String(window.location.hash || '').split('?')[0].replace(/\/+$/, '').toLowerCase();
            return hash === '' ||
                hash === '#' ||
                hash === '#/home' ||
                hash === '#/home.html' ||
                hash === '#home' ||
                hash === '#home.html' ||
                hash.indexOf('#/home') === 0;
        },

        findTabSlider: function () {
            return document.querySelector('.headerTabs .emby-tabs-slider') ||
                document.querySelector('.skinHeader .emby-tabs-slider') ||
                document.querySelector('.emby-tabs-slider');
        },

        findTabsEl: function () {
            return document.querySelector('.headerTabs [is="emby-tabs"]') ||
                document.querySelector('.skinHeader [is="emby-tabs"]') ||
                document.querySelector('[is="emby-tabs"]');
        },

        watchHeaderTabs: function () {
            if (this._headerWatcherBound) {
                return;
            }
            this._headerWatcherBound = true;
            const self = this;
            const host = document.querySelector('.skinHeader') || document.body;
            if (!host) {
                return;
            }
            new MutationObserver(function () {
                if (!self.isHomeHash()) {
                    return;
                }
                if (self._headerEnsureTimer) {
                    clearTimeout(self._headerEnsureTimer);
                }
                self._headerEnsureTimer = setTimeout(function () {
                    self.ensureNativeTabs();
                }, 200);
            }).observe(host, { childList: true, subtree: true });
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
            document.querySelectorAll('.headerTabs [data-jellyspot-tab], #jellyspot-header-tabs').forEach(function (btn) {
                btn.remove();
            });
        },

        removeFallbackBar: function () {
            const bar = document.getElementById('jellyspot-header-tabs');
            if (bar) {
                bar.remove();
            }
        },

        ensureFallbackBar: function (page) {
            const self = this;
            let bar = document.getElementById('jellyspot-header-tabs');
            const host = document.querySelector('.headerTabs') ||
                document.querySelector('.skinHeader') ||
                page;
            if (!host) {
                return;
            }
            if (!bar) {
                bar = document.createElement('div');
                bar.id = 'jellyspot-header-tabs';
                bar.className = 'jellyspot-header-tabs';
                Object.keys(self.TAB_DEFS).forEach(function (id) {
                    const button = document.createElement('button');
                    button.type = 'button';
                    button.className = 'jellyspot-header-tab';
                    button.setAttribute('data-jellyspot-tab', id);
                    button.textContent = self.TAB_DEFS[id].defaultTitle;
                    button.addEventListener('click', function () {
                        self.showPluginTab(id);
                        const native = document.querySelector('.headerTabs [data-jellyspot-tab="' + id + '"]');
                        if (native && native !== button && typeof native.click === 'function') {
                            native.click();
                        }
                    });
                    bar.appendChild(button);
                });
                host.appendChild(bar);
                log.info('fallback Home text tabs attached');
            }
            if (page) {
                Object.keys(self.TAB_DEFS).forEach(function (id) {
                    if (!page.querySelector('.tabContent[data-jellyspot-tab="' + id + '"]')) {
                        page.appendChild(self.createTabPanel(id));
                    }
                });
            }
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

                self.patchForeignTabManagers();
                const page = document.getElementById('indexPage');
                const tabsSlider = self.findTabSlider();
                const tabsEl = self.findTabsEl();
                if (!page || !self.homePageExists()) {
                    self._tabsEnsureQueued = true;
                    return;
                }

                if (!tabsSlider) {
                    self.ensureFallbackBar(page);
                    return;
                }

                if (tabsEl) {
                    self.attachPluginTabGuard(tabsEl);
                }
                const changed = self.applyTabs(page, tabsSlider);
                if (changed) {
                    log.info('native tab bar ready: browse, liked, sync, queue');
                    if (tabsEl && typeof tabsEl.refresh === 'function') {
                        try {
                            tabsEl.refresh();
                        } catch (err) {
                            // scroller refresh is best-effort
                        }
                    }
                }
                if (!tabsSlider.querySelector('[data-jellyspot-tab]')) {
                    self.ensureFallbackBar(page);
                } else {
                    self.removeFallbackBar();
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
            if (hash.indexOf('/dashboard') >= 0 ||
                hash.indexOf('configurationpage') >= 0 ||
                hash.indexOf('/settings') >= 0) {
                return true;
            }
            const page = document.querySelector('.page:not(.hide)');
            return !!(page && (
                page.classList.contains('type-interior') ||
                page.classList.contains('dashboardDocument') ||
                page.classList.contains('pluginConfigurationPage') ||
                page.id === 'jellySpotConfigurationPage'
            ));
        },

        isPluginSettingsContext: function () {
            const hash = String(window.location.hash || '').toLowerCase();
            if (hash.indexOf('configurationpage') >= 0 && hash.indexOf('jellyspot') >= 0) {
                return true;
            }
            const page = document.getElementById('jellySpotConfigurationPage');
            return !!(page && !page.classList.contains('hide'));
        },

        isJellySpotMenuHref: function (href) {
            const value = String(href || '').toLowerCase();
            return value.indexOf('name=jellyspot') >= 0 ||
                (value.indexOf('configurationpage') >= 0 && value.indexOf('jellyspot') >= 0);
        },

        isDrawerElement: function (el) {
            return !!(el && el.closest && el.closest(
                '.MuiDrawer-root, .mainDrawer, .navDrawer, .mainDrawer-scrollContainer, aside'
            ));
        },

        isJellySpotDrawerItem: function (el) {
            if (!el || (el.getAttribute && el.getAttribute('data-jellyspot-tab'))) {
                return false;
            }
            if (el.id === 'jellyspot-drawer-browse' || el.getAttribute('data-jellyspot-rewritten') === '1') {
                return true;
            }
            const href = el.getAttribute('href') || el.getAttribute('to') || '';
            if (this.isJellySpotMenuHref(href)) {
                return this.isDrawerElement(el) || !this.isDashboardContext();
            }
            if (!this.isDrawerElement(el)) {
                return false;
            }
            const text = String(el.textContent || '').replace(/\s+/g, ' ').trim();
            return text === 'JellySpot';
        },

        hideDrawerEntry: function () {
            // Official left-menu item is Dashboard settings. Do not hide it.
        },

        bindMenuInterceptor: function () {
            if (this._menuInterceptorBound) {
                return;
            }
            this._menuInterceptorBound = true;
        },

        setupNativeTabWatchers: function () {
            const self = this;
            log.info('native tab watchers ready');

            document.addEventListener('viewshow', function (event) {
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

        linkedName: function (me) {
            return this.pick(me, 'SpotifyDisplayName', 'spotifyDisplayName')
                || this.pick(me, 'SpotifyUserId', 'spotifyUserId')
                || 'Spotify';
        },

        isLinked: function (me) {
            return this.pick(me, 'Linked', 'linked') === true;
        },

        stopLinkWatch: function () {
            if (this._linkPollTimer) {
                clearInterval(this._linkPollTimer);
                this._linkPollTimer = null;
            }
            if (this._linkFocusHandler) {
                window.removeEventListener('focus', this._linkFocusHandler);
                this._linkFocusHandler = null;
            }
        },

        watchLink: function (onLinked) {
            const self = this;
            self.stopLinkWatch();
            let tries = 0;
            function check() {
                ApiClient.ajax({ type: 'GET', url: ApiClient.getUrl('JellySpot/Me'), dataType: 'json' })
                    .then(function (me) {
                        me = self.coercePayload(me) || {};
                        if (!self.isLinked(me)) {
                            return;
                        }
                        self.stopLinkWatch();
                        onLinked(me);
                    });
            }
            self._linkFocusHandler = check;
            window.addEventListener('focus', check);
            self._linkPollTimer = setInterval(function () {
                tries += 1;
                if (tries > 90) {
                    self.stopLinkWatch();
                    return;
                }
                check();
            }, 2000);
        },

        startSpotifyLink: function (statusEl, onLinked) {
            const self = this;
            ApiClient.ajax({ type: 'GET', url: ApiClient.getUrl('JellySpot/OAuth/Start'), dataType: 'json' })
                .then(function (res) {
                    window.open(self.pick(res, 'Url', 'url'), '_blank', 'noopener');
                    if (statusEl) {
                        statusEl.textContent = 'Finish signing in with Spotify, then this page will continue.';
                    }
                    self.watchLink(onLinked);
                })
                .catch(function () {
                    if (statusEl) {
                        statusEl.textContent = 'Could not start Spotify login. An admin needs to set the Spotify Client ID under Dashboard → Plugins → JellySpot.';
                    }
                });
        },

        renderLinkGate: function (root, onLinked) {
            const self = this;
            root.innerHTML =
                '<div class="jellyspot-app">' +
                '<div class="jellyspot-hero"><div>' +
                '<div class="jellyspot-kicker">Your account</div>' +
                '<h2 class="sectionTitle jellyspot-title">Link Spotify</h2>' +
                '<p class="jellyspot-lede">Each Jellyfin user links their own Spotify. After that, Browse shows your playlists and liked songs — not anyone else\'s.</p>' +
                '</div></div>' +
                '<div class="jellyspot-toolbar jellyspot-toolbar-primary">' +
                '<button is="emby-button" type="button" class="raised button-submit jellyspot-link-btn"><span>Link my Spotify</span></button>' +
                '</div>' +
                '<div class="jellyspot-status jellyspot-link-status">Not linked yet.</div>' +
                '</div>';
            const statusEl = root.querySelector('.jellyspot-link-status');
            root.querySelector('.jellyspot-link-btn').addEventListener('click', function () {
                self.startSpotifyLink(statusEl, onLinked);
            });
        },

        requireLinked: function (root, onLinked) {
            const self = this;
            ApiClient.ajax({ type: 'GET', url: ApiClient.getUrl('JellySpot/Me'), dataType: 'json' })
                .then(function (me) {
                    me = self.coercePayload(me) || {};
                    if (self.isLinked(me)) {
                        onLinked(me);
                        return;
                    }
                    delete root.dataset.jellyspotMounted;
                    self.renderLinkGate(root, function (linkedMe) {
                        delete root.dataset.jellyspotMounted;
                        onLinked(linkedMe);
                    });
                })
                .catch(function () {
                    delete root.dataset.jellyspotMounted;
                    self.renderLinkGate(root, function (linkedMe) {
                        delete root.dataset.jellyspotMounted;
                        onLinked(linkedMe);
                    });
                });
        },

        renderBrowse: function (root) {
            const self = this;
            this.requireLinked(root, function (me) {
                if (root.dataset.jellyspotMounted) {
                    return;
                }
                root.dataset.jellyspotMounted = 'true';
                const who = self.escapeHtml(self.linkedName(me));
                root.innerHTML =
                    '<div class="jellyspot-app">' +
                    '<div class="jellyspot-hero"><div>' +
                    '<div class="jellyspot-kicker">Your library</div>' +
                    '<h2 class="sectionTitle jellyspot-title">Browse</h2>' +
                    '<p class="jellyspot-lede">Signed in as ' + who + '. Playlists, saved albums, and artists you follow — pick tracks and queue only those.</p>' +
                    '</div></div>' +
                    '<div class="jellyspot-identity" hidden></div>' +
                    '<div class="jellyspot-toolbar jellyspot-toolbar-primary">' +
                    '<div class="jellyspot-search"><input type="search" class="jellyspot-search-input" placeholder="Search tracks, albums, playlists, artists" /></div>' +
                    '<button is="emby-button" type="button" class="raised button-submit jellyspot-search-btn"><span>Search</span></button>' +
                    '</div>' +
                    '<div class="jellyspot-libnav" role="tablist">' +
                    '<button type="button" class="jellyspot-libnav-btn is-active" data-lib="home">Overview</button>' +
                    '<button type="button" class="jellyspot-libnav-btn" data-lib="playlists">Playlists</button>' +
                    '<button type="button" class="jellyspot-libnav-btn" data-lib="albums">Albums</button>' +
                    '<button type="button" class="jellyspot-libnav-btn" data-lib="artists">Artists</button>' +
                    '<button type="button" class="jellyspot-libnav-btn" data-lib="liked">Liked</button>' +
                    '</div>' +
                    '<div class="jellyspot-status jellyspot-browse-status">Loading your library…</div>' +
                    '<div class="jellyspot-stage jellyspot-browse-results"></div>' +
                    '</div>';

                root.querySelector('.jellyspot-search-btn').addEventListener('click', function () {
                    self.searchBrowse(root);
                });
                root.querySelector('.jellyspot-search-input').addEventListener('keydown', function (e) {
                    if (e.key === 'Enter') {
                        e.preventDefault();
                        self.searchBrowse(root);
                    }
                });
                root.querySelectorAll('.jellyspot-libnav-btn').forEach(function (btn) {
                    btn.addEventListener('click', function () {
                        self.showLibrarySection(root, btn.getAttribute('data-lib'));
                    });
                });
                self.loadLibraryHome(root);
            });
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

        formatQueueMessage: function (res, fallback) {
            const queued = this.pick(res, 'Queued', 'queued');
            const skipped = this.pick(res, 'Skipped', 'skipped');
            if (queued == null && skipped == null) {
                return fallback;
            }
            let message = 'Queued ' + (queued || 0);
            if (skipped) {
                message += ', skipped ' + skipped + ' already in the library';
            }
            return message;
        },

        formatDuration: function (ms) {
            const total = Math.max(0, Math.round(Number(ms || 0) / 1000));
            const minutes = Math.floor(total / 60);
            return minutes + ':' + String(total % 60).padStart(2, '0');
        },

        addCatalogCard: function (grid, spec) {
            const self = this;
            const card = document.createElement('article');
            card.className = 'jellyspot-card' + (spec.open ? ' is-openable' : '') + (spec.avatar ? ' is-artist' : '');
            card.innerHTML =
                '<span class="jellyspot-card-kind">' + this.escapeHtml(spec.kindLabel) + '</span>' +
                (spec.image
                    ? '<img src="' + this.escapeHtml(spec.image) + '" alt="" />'
                    : '<div class="jellyspot-card-fallback"></div>') +
                '<strong>' + this.escapeHtml(spec.title) + '</strong>' +
                '<div class="fieldDescription">' + this.escapeHtml(spec.meta || '') + '</div>' +
                '<div class="jellyspot-card-actions"></div>';
            const actions = card.querySelector('.jellyspot-card-actions');
            if (spec.open) {
                const open = document.createElement('button');
                open.setAttribute('is', 'emby-button');
                open.type = 'button';
                open.className = 'raised button-submit';
                open.innerHTML = '<span>Open</span>';
                open.addEventListener('click', function (e) {
                    e.stopPropagation();
                    spec.open();
                });
                actions.appendChild(open);
                card.addEventListener('click', function (e) {
                    if (e.target.closest('button')) {
                        return;
                    }
                    spec.open();
                });
            }
            if (spec.queue) {
                const queue = document.createElement('button');
                queue.setAttribute('is', 'emby-button');
                queue.type = 'button';
                queue.className = 'raised';
                queue.innerHTML = '<span>' + (spec.open ? 'Queue all' : 'Queue') + '</span>';
                queue.addEventListener('click', function (e) {
                    e.stopPropagation();
                    spec.queue();
                });
                actions.appendChild(queue);
            }
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
                self.setBrowseStatus(root, self.formatQueueMessage(res, 'Queued ' + name));
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
            this.setBrowseStatus(root, 'Searching…');
            ApiClient.ajax({
                type: 'GET',
                url: ApiClient.getUrl('JellySpot/Search?q=' + encodeURIComponent(q) + '&type=track,album,playlist,artist&limit=10'),
                dataType: 'json'
            }).then(function (data) {
                data = self.coercePayload(data) || {};
                const stage = root.querySelector('.jellyspot-browse-results');
                stage.innerHTML = '';
                let count = 0;
                function addGroup(title, items, kind) {
                    const list = items || [];
                    if (!list.length) {
                        return;
                    }
                    const head = document.createElement('div');
                    head.className = 'jellyspot-section-head';
                    head.innerHTML = '<h3>' + self.escapeHtml(title) + '</h3><span class="jellyspot-muted">' + list.length + '</span>';
                    stage.appendChild(head);
                    const grid = document.createElement('div');
                    grid.className = 'jellyspot-grid';
                    list.forEach(function (item) {
                        count += 1;
                        const id = item.id || self.pick(item, 'Id');
                        const name = item.name || self.pick(item, 'Name') || 'Untitled';
                        self.addCatalogCard(grid, {
                            kindLabel: kind,
                            title: name,
                            meta: kind === 'artist'
                                ? 'Artist'
                                : (kind === 'track' || kind === 'album' ? self.artistsOf(item) : 'Playlist'),
                            image: self.coverOf(item),
                            avatar: kind === 'artist',
                            open: kind === 'playlist'
                                ? function () { self.openPlaylist(root, id, name); }
                                : kind === 'album'
                                    ? function () { self.openAlbum(root, id, name); }
                                    : kind === 'artist'
                                        ? function () { self.openArtist(root, id, name); }
                                        : null,
                            queue: kind === 'artist' ? null : function () { self.queueItem(kind, id, name, root); }
                        });
                    });
                    stage.appendChild(grid);
                }
                addGroup('Artists', data.artists && data.artists.items, 'artist');
                addGroup('Playlists', data.playlists && data.playlists.items, 'playlist');
                addGroup('Albums', data.albums && data.albums.items, 'album');
                addGroup('Tracks', data.tracks && data.tracks.items, 'track');
                self.setBrowseStatus(root, count ? count + ' results — open a playlist to choose tracks' : 'No results');
                if (!count) {
                    stage.innerHTML = '<div class="jellyspot-empty">No matches. Try a different title, artist, or playlist name.</div>';
                }
            }).catch(function () {
                self.setBrowseStatus(root, 'Search failed. Link your Spotify first.');
            });
        },

        artistsOf: function (item) {
            const artists = this.pick(item, 'Artists', 'artists') || [];
            if (Array.isArray(artists)) {
                return artists.map(function (artist) {
                    return typeof artist === 'string' ? artist : (artist && (artist.name || artist.Name)) || '';
                }).filter(Boolean).join(', ');
            }
            return String(artists);
        },

        loadLiked: function (root) {
            this.openLikedCollection(root);
        },

        renderLiked: function (root) {
            const self = this;
            this.requireLinked(root, function (me) {
                if (!root.dataset.jellyspotMounted) {
                    root.dataset.jellyspotMounted = 'true';
                    root.innerHTML =
                        '<div class="jellyspot-app">' +
                        '<div class="jellyspot-hero"><div>' +
                        '<div class="jellyspot-kicker">Your Spotify</div>' +
                        '<h2 class="sectionTitle jellyspot-title">Liked songs</h2>' +
                        '<p class="jellyspot-lede">Signed in as ' + self.escapeHtml(self.linkedName(me)) + '. Select the liked tracks you want.</p>' +
                        '</div></div>' +
                        '<div class="jellyspot-status jellyspot-browse-status">Loading liked songs…</div>' +
                        '<div class="jellyspot-stage jellyspot-browse-results jellyspot-liked-results"></div>' +
                        '</div>';
                }
                self.openLikedCollection(root);
            });
        },

        openLikedCollection: function (root) {
            const self = this;
            this.setLibraryNav(root, 'liked');
            this.setBrowseStatus(root, 'Loading liked songs…');
            ApiClient.ajax({ type: 'GET', url: ApiClient.getUrl('JellySpot/LikedSongs'), dataType: 'json' })
                .then(function (tracks) {
                    self.renderTrackPicker(root, {
                        kind: 'liked',
                        title: 'Liked songs',
                        subtitle: 'Saved in your Spotify library',
                        image: tracks && tracks[0] ? self.coverOf(tracks[0]) : '',
                        tracks: tracks || [],
                        queueAll: function () { self.queueItem('liked', 'liked', 'Liked Songs', root); },
                        playlistName: 'Liked Songs'
                    });
                })
                .catch(function () {
                    self.setBrowseStatus(root, 'Could not load liked songs.');
                });
        },

        coercePayload: function (data) {
            if (typeof data === 'string') {
                try {
                    return JSON.parse(data);
                } catch (err) {
                    return null;
                }
            }
            return data;
        },

        coerceTracks: function (tracks) {
            if (Array.isArray(tracks)) {
                return tracks;
            }
            if (tracks && Array.isArray(tracks.items)) {
                return tracks.items.map(function (item) {
                    return item && (item.track || item.Track) ? (item.track || item.Track) : item;
                }).filter(Boolean);
            }
            if (tracks && Array.isArray(tracks.$values)) {
                return tracks.$values;
            }
            return [];
        },

        openPlaylist: function (root, id, fallbackName) {
            const self = this;
            this.setBrowseStatus(root, 'Opening playlist…');
            ApiClient.ajax({ type: 'GET', url: ApiClient.getUrl('JellySpot/Playlists/' + encodeURIComponent(id)), dataType: 'json' })
                .then(function (data) {
                    data = self.coercePayload(data) || {};
                    const playlist = self.pick(data, 'Playlist', 'playlist') || {};
                    const tracks = self.coerceTracks(self.pick(data, 'Tracks', 'tracks'));
                    const name = self.pick(playlist, 'Name', 'name') || fallbackName || 'Playlist';
                    self.renderTrackPicker(root, {
                        kind: 'playlist',
                        id: id,
                        title: name,
                        subtitle: (self.pick(playlist, 'TrackCount', 'trackCount') || tracks.length) + ' tracks',
                        image: self.coverOf(playlist),
                        tracks: tracks,
                        queueAll: function () { self.queueItem('playlist', id, name, root); },
                        monitor: function () {
                            ApiClient.ajax({
                                type: 'POST',
                                url: ApiClient.getUrl('JellySpot/Playlists/' + encodeURIComponent(id) + '/Monitor')
                            }).then(function () {
                                self.setBrowseStatus(root, 'Monitoring ' + name + ' on sync');
                            });
                        },
                        playlistId: id,
                        playlistName: name
                    });
                })
                .catch(function () {
                    self.setBrowseStatus(root, 'Could not open that playlist.');
                });
        },

        openAlbum: function (root, id, fallbackName) {
            const self = this;
            this.setBrowseStatus(root, 'Opening album…');
            ApiClient.ajax({ type: 'GET', url: ApiClient.getUrl('JellySpot/Albums/' + encodeURIComponent(id)), dataType: 'json' })
                .then(function (data) {
                    data = self.coercePayload(data) || {};
                    const album = self.pick(data, 'Album', 'album') || {};
                    const tracks = self.coerceTracks(self.pick(data, 'Tracks', 'tracks'));
                    const name = self.pick(album, 'Name', 'name') || fallbackName || 'Album';
                    self.renderTrackPicker(root, {
                        kind: 'album',
                        id: id,
                        title: name,
                        subtitle: self.pick(album, 'Artists', 'artists') || '',
                        image: self.coverOf(album) || (tracks[0] && self.coverOf(tracks[0])),
                        tracks: tracks,
                        queueAll: function () { self.queueItem('album', id, name, root); },
                        playlistId: id,
                        playlistName: name
                    });
                })
                .catch(function () {
                    self.setBrowseStatus(root, 'Could not open that album.');
                });
        },

        renderTrackPicker: function (root, spec) {
            const self = this;
            const stage = root.querySelector('.jellyspot-browse-results') || root.querySelector('.jellyspot-liked-results');
            if (!stage) {
                return;
            }

            const tracks = this.coerceTracks(spec.tracks);
            if (!tracks.length) {
                stage.innerHTML = '<div class="jellyspot-empty">No readable tracks came back from Spotify.</div>';
                this.setBrowseStatus(root, '0 tracks loaded');
                return;
            }
            stage.innerHTML =
                '<div class="jellyspot-detail">' +
                '<div class="jellyspot-detail-head">' +
                (spec.image
                    ? '<img class="jellyspot-detail-cover" src="' + this.escapeHtml(spec.image) + '" alt="" />'
                    : '<div class="jellyspot-detail-fallback"></div>') +
                '<div class="jellyspot-detail-meta">' +
                '<div class="jellyspot-kicker">' + this.escapeHtml(spec.kind) + '</div>' +
                '<h3>' + this.escapeHtml(spec.title) + '</h3>' +
                '<p class="jellyspot-lede">' + this.escapeHtml(spec.subtitle || '') + '</p>' +
                '</div></div>' +
                '<div class="jellyspot-actionbar">' +
                (spec.kind !== 'liked' ? '<button is="emby-button" type="button" class="raised jellyspot-back-btn"><span>Back</span></button>' : '') +
                '<button is="emby-button" type="button" class="raised jellyspot-select-all"><span>Select all</span></button>' +
                '<button is="emby-button" type="button" class="raised jellyspot-select-none"><span>Select none</span></button>' +
                '<input type="search" class="jellyspot-track-filter" placeholder="Filter tracks" />' +
                '<button is="emby-button" type="button" class="raised button-submit jellyspot-queue-selected"><span>Queue selected</span></button>' +
                '<button is="emby-button" type="button" class="raised jellyspot-queue-all"><span>Queue all</span></button>' +
                (spec.monitor ? '<button is="emby-button" type="button" class="raised jellyspot-monitor-btn"><span>Monitor</span></button>' : '') +
                '<span class="jellyspot-count"></span>' +
                '</div>' +
                '<table class="jellyspot-track-table"><thead><tr>' +
                '<th></th><th>Title</th><th class="jellyspot-col-album">Album</th><th class="jellyspot-col-duration">Time</th><th>Status</th>' +
                '</tr></thead><tbody></tbody></table>' +
                '</div>';

            const tbody = stage.querySelector('tbody');
            tracks.forEach(function (track, index) {
                const id = self.pick(track, 'Id', 'id');
                const title = self.pick(track, 'Name', 'name') || 'Untitled';
                const artists = self.artistsOf(track);
                const album = self.pick(track, 'Album', 'album') || '';
                const duration = self.pick(track, 'DurationMs', 'durationMs', 'duration_ms') || 0;
                const image = self.coverOf(track);
                const tr = document.createElement('tr');
                tr.dataset.trackId = id;
                tr.dataset.search = (title + ' ' + artists + ' ' + album).toLowerCase();
                tr.innerHTML =
                    '<td><input type="checkbox" class="jellyspot-track-check" checked /></td>' +
                    '<td><div class="jellyspot-track-main">' +
                    (image ? '<img src="' + self.escapeHtml(image) + '" alt="" />' : '<div class="jellyspot-track-fallback"></div>') +
                    '<div><div class="jellyspot-track-title">' + self.escapeHtml(title) + '</div>' +
                    '<div class="jellyspot-muted">' + self.escapeHtml(artists) + '</div></div></div></td>' +
                    '<td class="jellyspot-col-album jellyspot-muted">' + self.escapeHtml(album) + '</td>' +
                    '<td class="jellyspot-col-duration jellyspot-muted">' + self.escapeHtml(self.formatDuration(duration)) + '</td>' +
                    '<td><span class="jellyspot-pill">Ready</span></td>';
                tr.addEventListener('click', function (e) {
                    if (e.target.closest('input, button')) {
                        return;
                    }
                    const box = tr.querySelector('.jellyspot-track-check');
                    box.checked = !box.checked;
                    self.syncTrackSelection(stage);
                });
                tr.querySelector('.jellyspot-track-check').addEventListener('change', function () {
                    self.syncTrackSelection(stage);
                });
                tbody.appendChild(tr);
            });

            const back = stage.querySelector('.jellyspot-back-btn');
            if (back) {
                back.addEventListener('click', function () {
                    self.loadLibraryHome(root);
                });
            }
            stage.querySelector('.jellyspot-select-all').addEventListener('click', function () {
                self.setVisibleTrackChecks(stage, true);
            });
            stage.querySelector('.jellyspot-select-none').addEventListener('click', function () {
                self.setVisibleTrackChecks(stage, false);
            });
            stage.querySelector('.jellyspot-track-filter').addEventListener('input', function (e) {
                self.filterTrackRows(stage, e.target.value);
            });
            stage.querySelector('.jellyspot-queue-selected').addEventListener('click', function () {
                const ids = self.selectedTrackIds(stage);
                if (!ids.length) {
                    self.setBrowseStatus(root, 'Select at least one track.');
                    return;
                }
                ApiClient.ajax({
                    type: 'POST',
                    url: ApiClient.getUrl('JellySpot/Queue/Tracks'),
                    contentType: 'application/json',
                    data: JSON.stringify({
                        TrackIds: ids,
                        PlaylistId: spec.playlistId || spec.kind,
                        PlaylistName: spec.playlistName || spec.title
                    })
                }).then(function (res) {
                    self.setBrowseStatus(root, self.formatQueueMessage(res, 'Queued selected tracks'));
                }).catch(function () {
                    self.setBrowseStatus(root, 'Queue failed — link Spotify under Sync first.');
                });
            });
            stage.querySelector('.jellyspot-queue-all').addEventListener('click', function () {
                spec.queueAll();
            });
            const monitor = stage.querySelector('.jellyspot-monitor-btn');
            if (monitor && spec.monitor) {
                monitor.addEventListener('click', spec.monitor);
            }

            this.syncTrackSelection(stage);
            this.setBrowseStatus(root, tracks.length + ' tracks — deselect anything you do not want');
            this.markOwnedTracks(stage);
        },

        selectedTrackIds: function (stage) {
            return Array.prototype.map.call(stage.querySelectorAll('tr[data-track-id]'), function (row) {
                const box = row.querySelector('.jellyspot-track-check');
                return box && box.checked && !row.classList.contains('is-hidden') ? row.dataset.trackId : null;
            }).filter(Boolean);
        },

        setVisibleTrackChecks: function (stage, checked) {
            stage.querySelectorAll('tr[data-track-id]').forEach(function (row) {
                if (row.classList.contains('is-hidden')) {
                    return;
                }
                row.querySelector('.jellyspot-track-check').checked = checked;
            });
            this.syncTrackSelection(stage);
        },

        filterTrackRows: function (stage, query) {
            const needle = String(query || '').trim().toLowerCase();
            stage.querySelectorAll('tr[data-track-id]').forEach(function (row) {
                row.classList.toggle('is-hidden', !!(needle && row.dataset.search.indexOf(needle) < 0));
            });
            this.syncTrackSelection(stage);
        },

        syncTrackSelection: function (stage) {
            const rows = stage.querySelectorAll('tr[data-track-id]');
            let selected = 0;
            let visible = 0;
            rows.forEach(function (row) {
                if (row.classList.contains('is-hidden')) {
                    return;
                }
                visible += 1;
                const box = row.querySelector('.jellyspot-track-check');
                row.classList.toggle('is-selected', !!(box && box.checked));
                if (box && box.checked) {
                    selected += 1;
                }
            });
            const count = stage.querySelector('.jellyspot-count');
            if (count) {
                count.textContent = selected + ' of ' + visible + ' selected';
            }
        },

        markOwnedTracks: function (stage) {
            const ids = Array.prototype.map.call(stage.querySelectorAll('tr[data-track-id]'), function (row) {
                return row.dataset.trackId;
            }).filter(Boolean);
            if (!ids.length) {
                return;
            }

            const chunks = [];
            for (let i = 0; i < ids.length; i += 80) {
                chunks.push(ids.slice(i, i + 80));
            }

            Promise.all(chunks.map(function (chunk) {
                return ApiClient.ajax({
                    type: 'GET',
                    url: ApiClient.getUrl('JellySpot/Library/Exists') + '?ids=' + encodeURIComponent(chunk.join(',')),
                    dataType: 'json'
                });
            })).then(function (maps) {
                const owned = {};
                maps.forEach(function (map) {
                    Object.keys(map || {}).forEach(function (key) {
                        owned[key] = map[key];
                    });
                });
                stage.querySelectorAll('tr[data-track-id]').forEach(function (row) {
                    const pill = row.querySelector('.jellyspot-pill');
                    if (!pill) {
                        return;
                    }
                    const isOwned = !!owned[row.dataset.trackId];
                    pill.textContent = isOwned ? 'In library' : 'Ready';
                    pill.classList.toggle('is-owned', isOwned);
                });
            }).catch(function () {
                // ownership badges are optional
            });
        },

        setLibraryNav: function (root, section) {
            root.querySelectorAll('.jellyspot-libnav-btn').forEach(function (btn) {
                btn.classList.toggle('is-active', btn.getAttribute('data-lib') === section);
            });
        },

        showLibrarySection: function (root, section) {
            const id = section || 'home';
            this.setLibraryNav(root, id);
            if (id === 'home') {
                this.loadLibraryHome(root);
            } else if (id === 'playlists') {
                this.loadPlaylists(root);
            } else if (id === 'albums') {
                this.loadSavedAlbums(root);
            } else if (id === 'artists') {
                this.loadFollowedArtists(root);
            } else {
                this.openLikedCollection(root);
            }
        },

        fillIdentity: function (root, data) {
            const bar = root.querySelector('.jellyspot-identity');
            if (!bar) {
                return;
            }
            const name = this.linkedName(data);
            const playlists = this.pick(data, 'PlaylistCount', 'playlistCount') || (this.pick(data, 'Playlists', 'playlists') || []).length;
            const albums = this.pick(data, 'AlbumCount', 'albumCount') || (this.pick(data, 'Albums', 'albums') || []).length;
            const artists = this.pick(data, 'ArtistCount', 'artistCount') || (this.pick(data, 'Artists', 'artists') || []).length;
            const liked = this.pick(data, 'LikedCount', 'likedCount') || 0;
            bar.hidden = false;
            bar.innerHTML =
                '<span class="jellyspot-id-name">' + this.escapeHtml(name) + '</span>' +
                '<span class="jellyspot-id-chip">' + playlists + ' playlists</span>' +
                '<span class="jellyspot-id-chip">' + albums + ' albums</span>' +
                '<span class="jellyspot-id-chip">' + artists + ' artists</span>' +
                '<span class="jellyspot-id-chip">' + liked + ' liked</span>';
        },

        addShelf: function (stage, title, count, onMore, renderCards) {
            const head = document.createElement('div');
            head.className = 'jellyspot-section-head';
            head.innerHTML = '<h3>' + this.escapeHtml(title) + '</h3><span class="jellyspot-muted">' + this.escapeHtml(String(count || 0)) + '</span>';
            if (onMore) {
                const more = document.createElement('button');
                more.type = 'button';
                more.className = 'jellyspot-more';
                more.textContent = 'View all';
                more.addEventListener('click', onMore);
                head.appendChild(more);
            }
            stage.appendChild(head);
            const row = document.createElement('div');
            row.className = 'jellyspot-shelf';
            renderCards(row);
            if (!row.children.length) {
                const empty = document.createElement('div');
                empty.className = 'jellyspot-empty is-inline';
                empty.textContent = 'Nothing here yet.';
                row.appendChild(empty);
            }
            stage.appendChild(row);
        },

        loadLibraryHome: function (root) {
            const self = this;
            this.setLibraryNav(root, 'home');
            this.setBrowseStatus(root, 'Loading your library…');
            ApiClient.ajax({ type: 'GET', url: ApiClient.getUrl('JellySpot/Library'), dataType: 'json' })
                .then(function (data) {
                    data = self.coercePayload(data) || {};
                    if (!self.isLinked(data)) {
                        delete root.dataset.jellyspotMounted;
                        self.renderLinkGate(root, function () {
                            delete root.dataset.jellyspotMounted;
                            self.renderBrowse(root);
                        });
                        return;
                    }
                    self.fillIdentity(root, data);
                    const stage = root.querySelector('.jellyspot-browse-results');
                    stage.innerHTML = '';
                    const playlists = self.pick(data, 'Playlists', 'playlists') || [];
                    const albums = self.pick(data, 'Albums', 'albums') || [];
                    const artists = self.pick(data, 'Artists', 'artists') || [];
                    const liked = self.pick(data, 'LikedPreview', 'likedPreview') || [];
                    const likedCount = self.pick(data, 'LikedCount', 'likedCount') || liked.length;

                    self.addShelf(stage, 'Liked songs', likedCount, function () {
                        self.showLibrarySection(root, 'liked');
                    }, function (row) {
                        liked.forEach(function (track) {
                            const id = self.pick(track, 'Id', 'id');
                            const name = self.pick(track, 'Name', 'name') || 'Untitled';
                            self.addCatalogCard(row, {
                                kindLabel: 'track',
                                title: name,
                                meta: self.artistsOf(track),
                                image: self.coverOf(track),
                                queue: function () { self.queueItem('track', id, name, root); }
                            });
                        });
                    });

                    self.addShelf(stage, 'Playlists', self.pick(data, 'PlaylistCount', 'playlistCount') || playlists.length, function () {
                        self.showLibrarySection(root, 'playlists');
                    }, function (row) {
                        playlists.forEach(function (p) {
                            const id = self.pick(p, 'Id', 'id');
                            const name = self.pick(p, 'Name', 'name') || id;
                            self.addCatalogCard(row, {
                                kindLabel: 'playlist',
                                title: name,
                                meta: (self.pick(p, 'TrackCount', 'trackCount') || 0) + ' tracks',
                                image: self.coverOf(p),
                                open: function () { self.openPlaylist(root, id, name); },
                                queue: function () { self.queueItem('playlist', id, name, root); }
                            });
                        });
                    });

                    self.addShelf(stage, 'Saved albums', self.pick(data, 'AlbumCount', 'albumCount') || albums.length, function () {
                        self.showLibrarySection(root, 'albums');
                    }, function (row) {
                        albums.forEach(function (album) {
                            const id = self.pick(album, 'Id', 'id');
                            const name = self.pick(album, 'Name', 'name') || id;
                            self.addCatalogCard(row, {
                                kindLabel: self.pick(album, 'AlbumType', 'albumType') || 'album',
                                title: name,
                                meta: (self.artistsOf(album) || '') + (self.pick(album, 'Year', 'year') ? ' · ' + self.pick(album, 'Year', 'year') : ''),
                                image: self.coverOf(album),
                                open: function () { self.openAlbum(root, id, name); },
                                queue: function () { self.queueItem('album', id, name, root); }
                            });
                        });
                    });

                    self.addShelf(stage, 'Artists you follow', self.pick(data, 'ArtistCount', 'artistCount') || artists.length, function () {
                        self.showLibrarySection(root, 'artists');
                    }, function (row) {
                        artists.forEach(function (artist) {
                            const id = self.pick(artist, 'Id', 'id');
                            const name = self.pick(artist, 'Name', 'name') || id;
                            const genres = self.pick(artist, 'Genres', 'genres') || [];
                            self.addCatalogCard(row, {
                                kindLabel: 'artist',
                                title: name,
                                meta: (Array.isArray(genres) ? genres.slice(0, 2).join(' · ') : '') || 'Artist',
                                image: self.coverOf(artist),
                                avatar: true,
                                open: function () { self.openArtist(root, id, name); }
                            });
                        });
                    });

                    self.setBrowseStatus(root, 'Your library — open anything, or search across Spotify');
                })
                .catch(function () {
                    self.setBrowseStatus(root, 'Could not load your library.');
                });
        },

        loadSavedAlbums: function (root) {
            const self = this;
            this.setLibraryNav(root, 'albums');
            this.setBrowseStatus(root, 'Loading saved albums…');
            ApiClient.ajax({ type: 'GET', url: ApiClient.getUrl('JellySpot/Library/Albums'), dataType: 'json' })
                .then(function (albums) {
                    albums = self.coerceTracks(self.coercePayload(albums)) || albums || [];
                    const stage = root.querySelector('.jellyspot-browse-results');
                    stage.innerHTML = '';
                    const list = Array.isArray(albums) ? albums : [];
                    if (!list.length) {
                        stage.innerHTML = '<div class="jellyspot-empty">No saved albums on this Spotify account.</div>';
                        self.setBrowseStatus(root, 'No saved albums');
                        return;
                    }
                    const grid = document.createElement('div');
                    grid.className = 'jellyspot-grid';
                    list.forEach(function (album) {
                        const id = self.pick(album, 'Id', 'id');
                        const name = self.pick(album, 'Name', 'name') || id;
                        self.addCatalogCard(grid, {
                            kindLabel: self.pick(album, 'AlbumType', 'albumType') || 'album',
                            title: name,
                            meta: (self.artistsOf(album) || '') + (self.pick(album, 'Year', 'year') ? ' · ' + self.pick(album, 'Year', 'year') : ''),
                            image: self.coverOf(album),
                            open: function () { self.openAlbum(root, id, name); },
                            queue: function () { self.queueItem('album', id, name, root); }
                        });
                    });
                    stage.appendChild(grid);
                    self.setBrowseStatus(root, list.length + ' saved albums');
                })
                .catch(function () {
                    self.setBrowseStatus(root, 'Could not load saved albums.');
                });
        },

        loadFollowedArtists: function (root) {
            const self = this;
            this.setLibraryNav(root, 'artists');
            this.setBrowseStatus(root, 'Loading artists…');
            ApiClient.ajax({ type: 'GET', url: ApiClient.getUrl('JellySpot/Library/Artists'), dataType: 'json' })
                .then(function (artists) {
                    artists = self.coercePayload(artists);
                    const stage = root.querySelector('.jellyspot-browse-results');
                    stage.innerHTML = '';
                    const list = Array.isArray(artists) ? artists : [];
                    if (!list.length) {
                        stage.innerHTML = '<div class="jellyspot-empty">You are not following any artists yet.</div>';
                        self.setBrowseStatus(root, 'No followed artists');
                        return;
                    }
                    const grid = document.createElement('div');
                    grid.className = 'jellyspot-grid';
                    list.forEach(function (artist) {
                        const id = self.pick(artist, 'Id', 'id');
                        const name = self.pick(artist, 'Name', 'name') || id;
                        const genres = self.pick(artist, 'Genres', 'genres') || [];
                        self.addCatalogCard(grid, {
                            kindLabel: 'artist',
                            title: name,
                            meta: (Array.isArray(genres) ? genres.slice(0, 2).join(' · ') : '') || 'Artist',
                            image: self.coverOf(artist),
                            avatar: true,
                            open: function () { self.openArtist(root, id, name); }
                        });
                    });
                    stage.appendChild(grid);
                    self.setBrowseStatus(root, list.length + ' artists');
                })
                .catch(function () {
                    self.setBrowseStatus(root, 'Could not load artists.');
                });
        },

        openArtist: function (root, id, fallbackName) {
            const self = this;
            this.setBrowseStatus(root, 'Opening artist…');
            ApiClient.ajax({ type: 'GET', url: ApiClient.getUrl('JellySpot/Artists/' + encodeURIComponent(id)), dataType: 'json' })
                .then(function (data) {
                    data = self.coercePayload(data) || {};
                    const artist = self.pick(data, 'Artist', 'artist') || {};
                    const albums = self.pick(data, 'Albums', 'albums') || [];
                    const name = self.pick(artist, 'Name', 'name') || fallbackName || 'Artist';
                    const genres = self.pick(artist, 'Genres', 'genres') || [];
                    const genreText = Array.isArray(genres) ? genres.slice(0, 4).join(' · ') : '';
                    const stage = root.querySelector('.jellyspot-browse-results');
                    stage.innerHTML =
                        '<div class="jellyspot-detail">' +
                        '<div class="jellyspot-detail-head is-artist">' +
                        (self.coverOf(artist)
                            ? '<img class="jellyspot-detail-cover is-round" src="' + self.escapeHtml(self.coverOf(artist)) + '" alt="" />'
                            : '<div class="jellyspot-detail-fallback is-round"></div>') +
                        '<div class="jellyspot-detail-meta">' +
                        '<div class="jellyspot-kicker">artist</div>' +
                        '<h3>' + self.escapeHtml(name) + '</h3>' +
                        '<p class="jellyspot-lede">' + self.escapeHtml(genreText) + '</p>' +
                        '<button is="emby-button" type="button" class="raised jellyspot-back-btn"><span>Back</span></button>' +
                        '</div></div></div>';
                    const grid = document.createElement('div');
                    grid.className = 'jellyspot-grid';
                    albums.forEach(function (album) {
                        const albumId = self.pick(album, 'Id', 'id');
                        const albumName = self.pick(album, 'Name', 'name') || albumId;
                        self.addCatalogCard(grid, {
                            kindLabel: self.pick(album, 'AlbumType', 'albumType') || 'album',
                            title: albumName,
                            meta: (self.pick(album, 'Year', 'year') || '') + (self.pick(album, 'TrackCount', 'trackCount') ? ' · ' + self.pick(album, 'TrackCount', 'trackCount') + ' tracks' : ''),
                            image: self.coverOf(album),
                            open: function () { self.openAlbum(root, albumId, albumName); },
                            queue: function () { self.queueItem('album', albumId, albumName, root); }
                        });
                    });
                    stage.appendChild(grid);
                    const back = stage.querySelector('.jellyspot-back-btn');
                    if (back) {
                        back.addEventListener('click', function () {
                            self.showLibrarySection(root, 'artists');
                        });
                    }
                    self.setBrowseStatus(root, albums.length + ' releases — open an album to pick tracks');
                })
                .catch(function () {
                    self.setBrowseStatus(root, 'Could not open that artist.');
                });
        },

        loadPlaylists: function (root) {
            const self = this;
            this.setLibraryNav(root, 'playlists');
            this.setBrowseStatus(root, 'Loading playlists…');
            ApiClient.ajax({ type: 'GET', url: ApiClient.getUrl('JellySpot/Playlists'), dataType: 'json' })
                .then(function (playlists) {
                    const stage = root.querySelector('.jellyspot-browse-results');
                    stage.innerHTML = '';
                    const list = playlists || [];
                    if (!list.length) {
                        stage.innerHTML = '<div class="jellyspot-empty">No playlists found for your Spotify account.</div>';
                        self.setBrowseStatus(root, 'No playlists');
                        return;
                    }
                    const grid = document.createElement('div');
                    grid.className = 'jellyspot-grid';
                    list.forEach(function (p) {
                        const id = self.pick(p, 'Id', 'id');
                        const name = self.pick(p, 'Name', 'name') || id;
                        const meta = (self.pick(p, 'TrackCount', 'trackCount') || 0) + ' tracks';
                        self.addCatalogCard(grid, {
                            kindLabel: 'playlist',
                            title: name,
                            meta: meta,
                            image: self.coverOf(p),
                            open: function () { self.openPlaylist(root, id, name); },
                            queue: function () { self.queueItem('playlist', id, name, root); }
                        });
                    });
                    stage.appendChild(grid);
                    self.setBrowseStatus(root, list.length + ' playlists — open one to choose tracks');
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
                    '<div class="jellyspot-hero"><div>' +
                    '<div class="jellyspot-kicker">Account</div>' +
                    '<h2 class="sectionTitle jellyspot-title">Sync</h2>' +
                    '<p class="jellyspot-lede">Link your Spotify account. Other Jellyfin users keep their own links and playlists.</p>' +
                    '</div></div>' +
                    '<p class="jellyspot-status">This link is only for your Jellyfin user.</p>' +
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
                    '<div class="jellyspot-hero"><div>' +
                    '<div class="jellyspot-kicker">Downloads</div>' +
                    '<h2 class="sectionTitle jellyspot-title">Queue</h2>' +
                    '<p class="jellyspot-lede">Track match scores, retry failures, and rematch anything that landed on the wrong video.</p>' +
                    '</div></div>' +
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
                    '<button is="emby-button" type="button" class="raised button-submit jellyspot-queue-retry-failed"><span>Retry failed</span></button>' +
                    '</div>' +
                    '<table class="detailTable jellyspot-queue-table"><thead><tr>' +
                    '<th>Track</th><th>Status</th><th>Score</th><th>Details</th><th></th>' +
                    '</tr></thead><tbody class="jellyspot-queue-body"></tbody></table>';

                root.querySelector('.jellyspot-queue-refresh').addEventListener('click', function () {
                    self.loadQueue(root);
                });
                root.querySelector('.jellyspot-queue-retry-failed').addEventListener('click', function () {
                    ApiClient.ajax({
                        type: 'POST',
                        url: ApiClient.getUrl('JellySpot/Queue/RetryFailed')
                    }).then(function () {
                        self.loadQueue(root);
                    });
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
                    if (st === 'Failed') {
                        const retry = document.createElement('button');
                        retry.setAttribute('is', 'emby-button');
                        retry.className = 'raised button-submit';
                        retry.type = 'button';
                        retry.innerHTML = '<span>Retry</span>';
                        retry.addEventListener('click', function () {
                            ApiClient.ajax({
                                type: 'POST',
                                url: ApiClient.getUrl('JellySpot/Queue/' + id + '/Retry')
                            }).then(function () {
                                self.loadQueue(root);
                            });
                        });
                        tr.children[4].appendChild(retry);
                    }
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
        var icon = document.querySelector('a[href*="name=JellySpot"] .MuiListItemIcon-root, a[href*="name=jellyspot"] .MuiListItemIcon-root');
        if (!icon || icon.dataset.jellyspotMenuIcon) {
            return;
        }
        icon.dataset.jellyspotMenuIcon = '1';
        icon.innerHTML = '<span class="material-icons notranslate MuiIcon-root MuiIcon-fontSizeMedium" aria-hidden="true">settings</span>';
    }

    if (document.body) {
        new MutationObserver(patch).observe(document.body, { childList: true, subtree: true });
        patch();
    }
})();

