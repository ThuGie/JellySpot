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
            browse: { sectionClass: 'jellyspot-browse-sections', defaultTitle: 'Spotify Browse' }
        },
        INNER_SECTIONS: {
            home: true,
            liked: true,
            sync: true,
            queue: true,
            playlists: true,
            albums: true,
            artists: true,
            recent: true,
            'top-tracks': true,
            'top-artists': true
        },
        SHELF_PREVIEW: 8,

        _watchersReady: false,
        _pendingInner: null,
        _browseBack: null,
        _modalStack: [],
        _modalRestore: null,
        _queuePollTimer: null,
        _searchState: null,
        _browseRoot: null,
        _escBound: false,
        _topTrackRange: 'medium_term',
        _topArtistRange: 'medium_term',
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
                    const page = document.getElementById('indexPage');
                    if (page && tabsSlider) {
                        self.alignPluginTab(page, tabsSlider);
                    }
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
                host.appendChild(bar);
                log.info('fallback Home text tabs attached');
            }
            bar.querySelectorAll('[data-jellyspot-tab]').forEach(function (btn) {
                if (!self.TAB_DEFS[btn.getAttribute('data-jellyspot-tab')]) {
                    btn.remove();
                }
            });
            Object.keys(self.TAB_DEFS).forEach(function (id) {
                let button = bar.querySelector('[data-jellyspot-tab="' + id + '"]');
                if (!button) {
                    button = document.createElement('button');
                    button.type = 'button';
                    button.className = 'jellyspot-header-tab';
                    button.setAttribute('data-jellyspot-tab', id);
                    button.addEventListener('click', function () {
                        self.showPluginTab(id);
                        const native = document.querySelector('.headerTabs [data-jellyspot-tab="' + id + '"]');
                        if (native && native !== button && typeof native.click === 'function') {
                            native.click();
                        }
                    });
                    bar.appendChild(button);
                }
                button.textContent = self.TAB_DEFS[id].defaultTitle;
            });
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

        bindPluginTabClick: function (button, id) {
            const self = this;
            if (!button || button.dataset.jellyspotClickBound === 'true') {
                return;
            }
            button.dataset.jellyspotClickBound = 'true';
            button.addEventListener('click', function () {
                self.showPluginTab(id);
            }, true);
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
            this.bindPluginTabClick(button, id);
            return button;
        },

        usedTabIndexes: function (page, tabsSlider, exceptButton, exceptPanel) {
            const used = {};
            function mark (el) {
                const n = parseInt(el.getAttribute('data-index'), 10);
                if (!isNaN(n)) {
                    used[n] = true;
                }
            }
            if (tabsSlider) {
                tabsSlider.querySelectorAll('.emby-tab-button').forEach(function (btn) {
                    if (btn !== exceptButton) {
                        mark(btn);
                    }
                });
            }
            if (page) {
                page.querySelectorAll('.tabContent').forEach(function (panel) {
                    if (panel !== exceptPanel) {
                        mark(panel);
                    }
                });
            }
            return used;
        },

        nextFreeTabIndex: function (used) {
            let max = -1;
            Object.keys(used).forEach(function (key) {
                const n = parseInt(key, 10);
                if (!isNaN(n) && n > max) {
                    max = n;
                }
            });
            return max + 1;
        },

        alignPluginTab: function (page, tabsSlider) {
            if (!page || !tabsSlider) {
                return false;
            }
            const button = tabsSlider.querySelector('[data-jellyspot-tab="browse"]');
            const panel = page.querySelector('.tabContent[data-jellyspot-tab="browse"]');
            if (!button || !panel) {
                return false;
            }
            this.bindPluginTabClick(button, 'browse');
            if (button.parentNode === tabsSlider && tabsSlider.lastElementChild !== button) {
                tabsSlider.appendChild(button);
            }
            const tabContents = page.querySelectorAll('.tabContent');
            if (tabContents[tabContents.length - 1] !== panel) {
                page.appendChild(panel);
            }
            const used = this.usedTabIndexes(page, tabsSlider, button, panel);
            const current = parseInt(button.getAttribute('data-index'), 10);
            const index = !isNaN(current) && !used[current] ? current : this.nextFreeTabIndex(used);
            const value = String(index);
            if (button.getAttribute('data-index') !== value || panel.getAttribute('data-index') !== value) {
                button.setAttribute('data-index', value);
                panel.setAttribute('data-index', value);
                return true;
            }
            panel.setAttribute('data-index', value);
            return false;
        },

        selectedHeaderButton: function (tabs, index) {
            return tabs.querySelector('.emby-tab-button-active') ||
                tabs.querySelector('.emby-tab-button[data-jellyspot-tab][data-index="' + index + '"]') ||
                tabs.querySelector('.emby-tab-button[data-index="' + index + '"]');
        },

        createTabPanel: function (id) {
            const panel = document.createElement('div');
            panel.className = 'tabContent pageTabContent';
            panel.setAttribute('data-jellyspot-tab', id);

            const sections = document.createElement('div');
            sections.className = 'sections padded-left padded-right ' + this.TAB_DEFS[id].sectionClass;
            panel.appendChild(sections);
            return panel;
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

                const selectedButton = self.selectedHeaderButton(tabs, index);
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

                const selectedButton = self.selectedHeaderButton(tabs, index);
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
                const selectedButton = self.selectedHeaderButton(tabs, index);
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
            page.classList.remove('jellyspot-is-open');
            page.querySelectorAll('.tabContent[data-jellyspot-tab]').forEach(function (panel) {
                panel.classList.add('hide');
                panel.classList.remove('is-active');
            });
        },

        showPluginTab: function (id) {
            const page = document.getElementById('indexPage');
            if (!page) {
                return;
            }
            const tab = this.TAB_DEFS[id] ? id : 'browse';
            page.classList.add('jellyspot-is-open');
            page.querySelectorAll('.tabContent[data-jellyspot-tab]').forEach(function (panel) {
                const active = panel.getAttribute('data-jellyspot-tab') === tab;
                panel.classList.toggle('hide', !active);
                panel.classList.toggle('is-active', active);
            });
            page.querySelectorAll('#homeTab, #favoritesTab, .tabContent[data-jellySeerr-tab], .tabContent[id^="customTab_"]').forEach(function (panel) {
                panel.classList.add('hide');
                panel.classList.remove('is-active');
            });
            this.mountTab(tab);
        },

        applyTabs: function (page, tabsSlider) {
            const self = this;
            let changed = false;

            tabsSlider.querySelectorAll('[data-jellyspot-tab]').forEach(function (btn) {
                if (!self.TAB_DEFS[btn.getAttribute('data-jellyspot-tab')]) {
                    btn.remove();
                    changed = true;
                }
            });
            page.querySelectorAll('.tabContent[data-jellyspot-tab]').forEach(function (panel) {
                if (!self.TAB_DEFS[panel.getAttribute('data-jellyspot-tab')]) {
                    panel.remove();
                    changed = true;
                }
            });

            Object.keys(self.TAB_DEFS).forEach(function (id) {
                let button = tabsSlider.querySelector('[data-jellyspot-tab="' + id + '"]');
                let panel = page.querySelector('.tabContent[data-jellyspot-tab="' + id + '"]');

                if (!button) {
                    button = self.createTabButton(id);
                    tabsSlider.appendChild(button);
                    changed = true;
                } else {
                    self.bindPluginTabClick(button, id);
                    const titleEl = button.querySelector('.emby-button-foreground');
                    const wanted = self.TAB_DEFS[id].defaultTitle;
                    if (titleEl && titleEl.textContent !== wanted) {
                        titleEl.textContent = wanted;
                        changed = true;
                    }
                }
                if (!panel) {
                    panel = self.createTabPanel(id);
                    page.appendChild(panel);
                    changed = true;
                }
                const sections = panel.querySelector('.sections');
                if (sections) {
                    sections.classList.add('padded-left', 'padded-right');
                }
            });

            if (self.alignPluginTab(page, tabsSlider)) {
                changed = true;
            }

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
                    log.info('native tab bar ready: Spotify Browse');
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

        requestedRawTab: function () {
            const hash = String(window.location.hash || '');
            const query = hash.indexOf('?') >= 0 ? hash.slice(hash.indexOf('?') + 1) : '';
            const fromHash = new URLSearchParams(query).get('tab');
            const fromStore = sessionStorage.getItem('jellyspotOpenTab');
            return fromHash || fromStore || null;
        },

        requestedTab: function () {
            const tab = this.requestedRawTab();
            if (!tab) {
                return null;
            }
            return this.TAB_DEFS[tab] || this.INNER_SECTIONS[tab] ? 'browse' : null;
        },

        requestedInner: function () {
            const tab = this.requestedRawTab();
            if (!tab || tab === 'browse' || !this.INNER_SECTIONS[tab]) {
                return null;
            }
            return tab;
        },

        openRequestedTab: function () {
            const tab = this.requestedTab();
            if (!tab) {
                return;
            }
            const button = document.querySelector('.headerTabs [data-jellyspot-tab="' + tab + '"]') ||
                document.querySelector('#jellyspot-header-tabs [data-jellyspot-tab="' + tab + '"]');
            if (!button) {
                return;
            }
            this._pendingInner = this.requestedInner();
            sessionStorage.removeItem('jellyspotOpenTab');
            this.showPluginTab(tab);
            if (typeof button.click === 'function') {
                button.click();
            }
        },

        openUserUi: function (tab) {
            const raw = tab && (this.TAB_DEFS[tab] || this.INNER_SECTIONS[tab]) ? tab : 'browse';
            sessionStorage.setItem('jellyspotOpenTab', raw);
            if (this.isHomeHash()) {
                this.ensureNativeTabs().then(() => this.openRequestedTab());
                return;
            }
            window.location.hash = '#/home?tab=' + encodeURIComponent(raw);
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
            this.renderBrowse(root);
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
                    self.applyPendingInner(root);
                    return;
                }
                root.dataset.jellyspotMounted = 'true';
                const who = self.escapeHtml(self.linkedName(me));
                root.innerHTML =
                    '<div class="jellyspot-app">' +
                    '<div class="jellyspot-toast-host" aria-live="polite"></div>' +
                    '<div class="sectionTitleContainer sectionTitleContainer-cards jellyspot-page-head">' +
                    '<h2 class="sectionTitle sectionTitle-cards">Spotify</h2>' +
                    '<div class="jellyspot-search">' +
                    '<input type="search" class="jellyspot-search-input" placeholder="Search songs, albums, playlists, artists" />' +
                    '<button type="button" class="jellyspot-search-clear" hidden aria-label="Clear search">×</button>' +
                    '</div>' +
                    '<div class="jellyspot-hero-who">' + who + '</div>' +
                    '</div>' +
                    '<div class="jellyspot-identity" hidden></div>' +
                    '<nav class="jellyspot-page-tabs" role="tablist">' +
                    '<button type="button" class="jellyspot-page-tab is-active" data-lib="home">Library</button>' +
                    '<button type="button" class="jellyspot-page-tab" data-lib="liked">Liked</button>' +
                    '<button type="button" class="jellyspot-page-tab" data-lib="sync">Sync</button>' +
                    '<button type="button" class="jellyspot-page-tab" data-lib="queue">Queue</button>' +
                    '</nav>' +
                    '<div class="jellyspot-library-chrome">' +
                    '<div class="jellyspot-status jellyspot-browse-status">Loading your library…</div>' +
                    '<div class="jellyspot-stage jellyspot-browse-results"></div>' +
                    '</div>' +
                    '<div class="jellyspot-pane hide" data-pane="sync"></div>' +
                    '<div class="jellyspot-pane hide" data-pane="queue"></div>' +
                    '</div>';

                let searchTimer = null;
                const searchInput = root.querySelector('.jellyspot-search-input');
                const searchClear = root.querySelector('.jellyspot-search-clear');
                self._browseRoot = root;
                self.bindAppKeys(root);
                function syncSearchClear() {
                    searchClear.hidden = !(searchInput.value || '').trim();
                }
                searchInput.addEventListener('keydown', function (e) {
                    if (e.key === 'Enter') {
                        e.preventDefault();
                        self.searchBrowse(root);
                    }
                });
                searchInput.addEventListener('input', function () {
                    clearTimeout(searchTimer);
                    syncSearchClear();
                    const q = (searchInput.value || '').trim();
                    if (!q) {
                        self._searchState = null;
                        self.showLibrarySection(root, 'home');
                        return;
                    }
                    searchTimer = setTimeout(function () {
                        self.searchBrowse(root);
                    }, 350);
                });
                searchClear.addEventListener('click', function () {
                    self.exitSearch(root);
                });
                root.querySelectorAll('.jellyspot-page-tab').forEach(function (btn) {
                    btn.addEventListener('click', function () {
                        self.clearSearch(root);
                        self.showLibrarySection(root, btn.getAttribute('data-lib'));
                    });
                });
                if (!self.applyPendingInner(root)) {
                    self.loadLibraryHome(root);
                }
            });
        },

        bindAppKeys: function (root) {
            this._browseRoot = root;
            if (this._escBound) {
                return;
            }
            this._escBound = true;
            const self = this;
            document.addEventListener('keydown', function (e) {
                if (e.key !== 'Escape') {
                    return;
                }
                const appRoot = self._browseRoot;
                if (!appRoot) {
                    return;
                }
                const open = appRoot.querySelector('.jellyspot-modal.is-open');
                if (open) {
                    e.preventDefault();
                    self.closeModal(appRoot);
                    return;
                }
                const input = appRoot.querySelector('.jellyspot-search-input');
                if (input && (input.value || '').trim()) {
                    e.preventDefault();
                    self.exitSearch(appRoot);
                }
            });
        },

        clearSearch: function (root) {
            const input = root.querySelector('.jellyspot-search-input');
            const clear = root.querySelector('.jellyspot-search-clear');
            if (input) {
                input.value = '';
            }
            if (clear) {
                clear.hidden = true;
            }
            this._searchState = null;
        },

        exitSearch: function (root) {
            this.clearSearch(root);
            this.closeModal(root, true);
            this.showLibrarySection(root, 'home');
        },

        insertLibraryBack: function (root, stage, onBack) {
            const self = this;
            const back = document.createElement('button');
            back.type = 'button';
            back.className = 'jellyspot-text-back jellyspot-back-btn';
            back.textContent = '← Library';
            back.addEventListener('click', onBack || function () {
                self.showLibrarySection(root, 'home');
            });
            stage.appendChild(back);
            return back;
        },

        setBrowseStatus: function (root, msg) {
            const el = root.querySelector('.jellyspot-browse-status');
            if (el) {
                el.textContent = msg || '';
            }
        },

        notify: function (root, msg, kind, action) {
            this.setBrowseStatus(root, msg);
            const host = root.querySelector('.jellyspot-toast-host');
            if (!host) {
                return;
            }
            host.innerHTML = '';
            const toast = document.createElement('div');
            toast.className = 'jellyspot-toast is-' + (kind || 'info');
            const text = document.createElement('span');
            text.textContent = msg;
            toast.appendChild(text);
            if (action && action.label && action.run) {
                const btn = document.createElement('button');
                btn.type = 'button';
                btn.className = 'jellyspot-toast-action';
                btn.textContent = action.label;
                btn.addEventListener('click', function () {
                    toast.remove();
                    action.run();
                });
                toast.appendChild(btn);
            }
            host.appendChild(toast);
            setTimeout(function () {
                toast.classList.add('is-leaving');
                setTimeout(function () {
                    toast.remove();
                }, 220);
            }, 4200);
        },

        showStageMessage: function (root, title, detail) {
            const stage = root.querySelector('.jellyspot-browse-results');
            if (!stage) {
                return;
            }
            stage.innerHTML = '<div class="jellyspot-empty"><strong>' + this.escapeHtml(title) + '</strong>' +
                (detail ? '<p>' + this.escapeHtml(detail) + '</p>' : '') + '</div>';
        },

        ensureModal: function (root) {
            const self = this;
            const app = root.querySelector('.jellyspot-app') || root;
            let modal = app.querySelector('.jellyspot-modal');
            if (modal) {
                return modal;
            }
            modal = document.createElement('div');
            modal.className = 'jellyspot-modal';
            modal.setAttribute('role', 'dialog');
            modal.setAttribute('aria-modal', 'true');
            modal.innerHTML =
                '<div class="jellyspot-modal-backdrop"></div>' +
                '<div class="jellyspot-modal-panel">' +
                '<button type="button" class="jellyspot-modal-close" aria-label="Close">×</button>' +
                '<div class="jellyspot-modal-body"></div>' +
                '</div>';
            app.appendChild(modal);
            modal.querySelector('.jellyspot-modal-backdrop').addEventListener('click', function () {
                self.closeModal(root, true);
            });
            modal.querySelector('.jellyspot-modal-close').addEventListener('click', function () {
                self.closeModal(root, true);
            });
            if (this._escBound) {
                return modal;
            }
            this._escBound = true;
            document.addEventListener('keydown', function (e) {
                if (e.key !== 'Escape') {
                    return;
                }
                const appRoot = self._browseRoot || root;
                const open = (appRoot && appRoot.querySelector)
                    ? appRoot.querySelector('.jellyspot-modal.is-open')
                    : document.querySelector('.jellyspot-modal.is-open');
                if (open) {
                    e.preventDefault();
                    self.closeModal(open.closest('.jellyspot-app') || appRoot || root);
                    return;
                }
                if (appRoot) {
                    const input = appRoot.querySelector('.jellyspot-search-input');
                    if (input && (input.value || '').trim()) {
                        e.preventDefault();
                        self.exitSearch(appRoot);
                    }
                }
            });
            return modal;
        },

        openModal: function (root, title, options) {
            options = options || {};
            const modal = this.ensureModal(root);
            if (!options.replace && modal.classList.contains('is-open') && typeof this._modalRestore === 'function') {
                this._modalStack.push(this._modalRestore);
            }
            modal.classList.add('is-open');
            if (title) {
                modal.setAttribute('aria-label', title);
            }
            document.body.classList.add('jellyspot-modal-open');
            return modal.querySelector('.jellyspot-modal-body');
        },

        closeModal: function (root, force) {
            if (!force && this._modalStack && this._modalStack.length) {
                const restore = this._modalStack.pop();
                this._modalRestore = null;
                if (typeof restore === 'function') {
                    restore();
                    return;
                }
            }
            this._modalStack = [];
            this._modalRestore = null;
            const app = (root && root.querySelector) ? (root.querySelector('.jellyspot-app') || root) : document;
            const modal = app.querySelector ? app.querySelector('.jellyspot-modal') : document.querySelector('.jellyspot-modal');
            if (modal) {
                modal.classList.remove('is-open');
                const body = modal.querySelector('.jellyspot-modal-body');
                if (body) {
                    body.innerHTML = '';
                }
            }
            document.body.classList.remove('jellyspot-modal-open');
        },

        showModalMessage: function (root, title, detail) {
            const body = this.openModal(root, title, { replace: true });
            body.innerHTML = '<div class="jellyspot-empty"><strong>' + this.escapeHtml(title) + '</strong>' +
                (detail ? '<p>' + this.escapeHtml(detail) + '</p>' : '') + '</div>';
        },

        goBrowseBack: function (root) {
            const back = this._browseBack;
            this._browseBack = null;
            if (typeof back === 'function') {
                back();
                return;
            }
            this.showLibrarySection(root, 'home');
        },

        coverOf: function (item) {
            return this.pick(item, 'CoverUrl', 'coverUrl', 'ImageUrl', 'imageUrl')
                || (item.album && item.album.images && item.album.images[0] && item.album.images[0].url)
                || (item.images && item.images[0] && item.images[0].url)
                || '';
        },

        albumIdOf: function (item) {
            if (!item) {
                return '';
            }
            const direct = this.pick(item, 'AlbumId', 'albumId');
            if (direct) {
                return direct;
            }
            const album = item.album || item.Album;
            if (album && typeof album === 'object') {
                return this.pick(album, 'Id', 'id') || '';
            }
            return '';
        },

        albumNameOf: function (item) {
            if (!item) {
                return '';
            }
            const album = item.album || item.Album;
            if (album && typeof album === 'object') {
                return this.pick(album, 'Name', 'name') || '';
            }
            return typeof album === 'string' ? album : '';
        },

        openTrackAlbum: function (root, item, fallbackName) {
            const self = this;
            const albumId = this.albumIdOf(item);
            const albumName = this.albumNameOf(item) || fallbackName || 'Album';
            if (albumId) {
                this.openAlbum(root, albumId, albumName);
                return;
            }
            const trackId = this.pick(item, 'Id', 'id');
            if (!trackId) {
                this.notify(root, 'That song has no album on Spotify.', 'error');
                return;
            }
            const host = this.openModal(root, albumName);
            host.innerHTML = '<div class="jellyspot-empty">Opening album…</div>';
            ApiClient.ajax({
                type: 'GET',
                url: ApiClient.getUrl('JellySpot/Tracks/' + encodeURIComponent(trackId)),
                dataType: 'json'
            }).then(function (track) {
                track = self.coercePayload(track) || {};
                const resolved = self.albumIdOf(track);
                if (!resolved) {
                    self.showModalMessage(root, 'No album', 'Spotify did not return an album for that song.');
                    return;
                }
                self.openAlbum(root, resolved, self.albumNameOf(track) || albumName, { replace: true });
            }).catch(function () {
                self.showModalMessage(root, 'Could not open that album', 'Spotify did not return album tracks.');
            });
        },

        addTrackCard: function (grid, root, item) {
            const self = this;
            const id = this.pick(item, 'Id', 'id');
            const name = this.pick(item, 'Name', 'name') || 'Untitled';
            this.addCatalogCard(grid, {
                kindLabel: 'track',
                title: name,
                meta: this.artistsOf(item),
                image: this.coverOf(item),
                trackId: id,
                open: function () { self.openTrackAlbum(root, item, name); },
                openLabel: 'Album',
                queueLabel: 'Queue',
                queue: function () { self.queueItem('track', id, name, root); }
            });
        },

        addArtistCard: function (grid, root, artist) {
            const self = this;
            const id = this.pick(artist, 'Id', 'id');
            const name = this.pick(artist, 'Name', 'name') || id;
            const genres = this.pick(artist, 'Genres', 'genres') || [];
            this.addCatalogCard(grid, {
                kindLabel: 'artist',
                title: name,
                meta: (Array.isArray(genres) ? genres.slice(0, 2).join(' · ') : '') || 'Artist',
                image: this.coverOf(artist),
                avatar: true,
                open: function () { self.openArtist(root, id, name); }
            });
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
            if (spec.trackId) {
                card.dataset.trackId = spec.trackId;
            }
            card.innerHTML =
                '<div class="jellyspot-card-art">' +
                (spec.image
                    ? '<img src="' + this.escapeHtml(spec.image) + '" alt="" />'
                    : '<div class="jellyspot-card-fallback"></div>') +
                '<span class="jellyspot-card-kind">' + this.escapeHtml(spec.kindLabel) + '</span>' +
                '<div class="jellyspot-card-overlay"></div>' +
                '</div>' +
                '<strong>' + this.escapeHtml(spec.title) + '</strong>' +
                '<div class="fieldDescription">' + this.escapeHtml(spec.meta || '') + '</div>';
            const overlay = card.querySelector('.jellyspot-card-overlay');
            if (spec.open) {
                const open = document.createElement('button');
                open.type = 'button';
                open.className = 'jellyspot-card-btn is-primary';
                open.textContent = spec.openLabel || 'Open';
                open.addEventListener('click', function (e) {
                    e.stopPropagation();
                    spec.open();
                });
                overlay.appendChild(open);
                card.addEventListener('click', function (e) {
                    if (e.target.closest('button')) {
                        return;
                    }
                    spec.open();
                });
            }
            if (spec.queue) {
                const queue = document.createElement('button');
                queue.type = 'button';
                queue.className = 'jellyspot-card-btn jellyspot-card-queue';
                queue.textContent = spec.queueLabel || (spec.open ? 'Queue all' : 'Queue');
                queue.addEventListener('click', function (e) {
                    e.stopPropagation();
                    spec.queue();
                });
                overlay.appendChild(queue);
            }
            grid.appendChild(card);
        },

        queueItem: function (kind, id, name, root) {
            const self = this;
            this.notify(root, 'Queuing ' + name + '…', 'info');
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
                self.notify(root, self.formatQueueMessage(res, 'Queued ' + name), 'ok', {
                    label: 'View queue',
                    run: function () { self.showLibrarySection(root, 'queue'); }
                });
            }).catch(function () {
                self.notify(root, 'Queue failed. Check Sync, then try again.', 'error');
            });
        },

        searchBrowse: function (root, more) {
            const self = this;
            const q = (root.querySelector('.jellyspot-search-input').value || '').trim();
            if (!q) {
                this._searchState = null;
                return;
            }
            if (!more || !this._searchState || this._searchState.q !== q) {
                this._searchState = {
                    q: q,
                    offset: 0,
                    artists: [],
                    playlists: [],
                    albums: [],
                    tracks: []
                };
            }
            const state = this._searchState;
            this.setBrowseStatus(root, more ? 'Loading more…' : 'Searching…');
            ApiClient.ajax({
                type: 'GET',
                url: ApiClient.getUrl('JellySpot/Search?q=' + encodeURIComponent(q) + '&type=track,album,playlist,artist&limit=10&offset=' + state.offset),
                dataType: 'json'
            }).then(function (data) {
                data = self.coercePayload(data) || {};
                function take(kind) {
                    const block = data[kind] || {};
                    return block.items || block.Items || [];
                }
                const page = {
                    artists: take('artists'),
                    playlists: take('playlists'),
                    albums: take('albums'),
                    tracks: take('tracks')
                };
                state.artists = state.artists.concat(page.artists);
                state.playlists = state.playlists.concat(page.playlists);
                state.albums = state.albums.concat(page.albums);
                state.tracks = state.tracks.concat(page.tracks);
                state.offset += 10;
                const total = state.artists.length + state.playlists.length + state.albums.length + state.tracks.length;
                const stage = root.querySelector('.jellyspot-browse-results');
                stage.innerHTML = '';
                self.insertLibraryBack(root, stage, function () {
                    self.exitSearch(root);
                });
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
                        const id = item.id || self.pick(item, 'Id');
                        const name = item.name || self.pick(item, 'Name') || 'Untitled';
                        if (kind === 'track') {
                            self.addTrackCard(grid, root, item);
                            return;
                        }
                        self.addCatalogCard(grid, {
                            kindLabel: kind,
                            title: name,
                            meta: kind === 'artist'
                                ? 'Artist'
                                : (kind === 'album' ? self.artistsOf(item) : 'Playlist'),
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
                addGroup('Artists', state.artists, 'artist');
                addGroup('Playlists', state.playlists, 'playlist');
                addGroup('Albums', state.albums, 'album');
                addGroup('Tracks', state.tracks, 'track');
                if (!total) {
                    const empty = document.createElement('div');
                    empty.className = 'jellyspot-empty';
                    empty.textContent = 'No matches. Try a different title, artist, or playlist name.';
                    stage.appendChild(empty);
                    self.setBrowseStatus(root, 'No results');
                    return;
                }
                if (page.artists.length >= 10 || page.playlists.length >= 10 || page.albums.length >= 10 || page.tracks.length >= 10) {
                    const moreBtn = document.createElement('button');
                    moreBtn.type = 'button';
                    moreBtn.className = 'jellyspot-card-btn jellyspot-load-more';
                    moreBtn.textContent = 'Load more';
                    moreBtn.addEventListener('click', function () {
                        self.searchBrowse(root, true);
                    });
                    stage.appendChild(moreBtn);
                }
                self.setBrowseStatus(root, total + ' results — open a song to see its album');
            }).catch(function () {
                self.setBrowseStatus(root, 'Search failed. Link your Spotify first.');
            });
        },

        artistsOf: function (item) {
            return this.artistEntriesOf(item).map(function (artist) {
                return artist.name;
            }).join(', ');
        },

        artistEntriesOf: function (item) {
            if (!item) {
                return [];
            }
            const raw = this.pick(item, 'Artists', 'artists') || [];
            if (Array.isArray(raw) && raw.length && typeof raw[0] === 'object') {
                return raw.map(function (artist) {
                    return {
                        id: (artist && (artist.id || artist.Id)) || '',
                        name: (artist && (artist.name || artist.Name)) || ''
                    };
                }).filter(function (artist) { return artist.name; });
            }
            const names = Array.isArray(raw)
                ? raw.filter(Boolean)
                : (raw ? String(raw).split(',').map(function (part) { return part.trim(); }).filter(Boolean) : []);
            const ids = this.pick(item, 'ArtistIds', 'artistIds') || [];
            return names.map(function (name, index) {
                return { id: ids[index] || '', name: name };
            });
        },

        artistLinksHtml: function (item) {
            const self = this;
            const entries = this.artistEntriesOf(item);
            if (!entries.length) {
                return '';
            }
            return entries.map(function (artist) {
                if (artist.id) {
                    return '<button type="button" class="jellyspot-link jellyspot-artist-link" data-artist-id="' +
                        self.escapeHtml(artist.id) + '" data-artist-name="' + self.escapeHtml(artist.name) + '">' +
                        self.escapeHtml(artist.name) + '</button>';
                }
                return '<span>' + self.escapeHtml(artist.name) + '</span>';
            }).join('<span class="jellyspot-muted">, </span>');
        },

        bindArtistLinks: function (root, host) {
            const self = this;
            host.querySelectorAll('.jellyspot-artist-link').forEach(function (btn) {
                btn.addEventListener('click', function (e) {
                    e.stopPropagation();
                    self.openArtist(root, btn.getAttribute('data-artist-id'), btn.getAttribute('data-artist-name'));
                });
            });
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

        unwrapPlaylistItem: function (item) {
            if (!item || typeof item !== 'object') {
                return item;
            }
            if (item.item || item.Item) {
                var wrapped = item.item || item.Item;
                if (wrapped && (wrapped.track || wrapped.Track)) {
                    return wrapped.track || wrapped.Track;
                }
                return wrapped;
            }
            if (item.track || item.Track) {
                return item.track || item.Track;
            }
            return item;
        },

        coerceTracks: function (tracks) {
            if (Array.isArray(tracks)) {
                return tracks.map(this.unwrapPlaylistItem.bind(this)).filter(Boolean);
            }
            if (tracks && Array.isArray(tracks.items)) {
                return tracks.items.map(this.unwrapPlaylistItem.bind(this)).filter(Boolean);
            }
            if (tracks && Array.isArray(tracks.$values)) {
                return tracks.$values.map(this.unwrapPlaylistItem.bind(this)).filter(Boolean);
            }
            return [];
        },

        openPlaylist: function (root, id, fallbackName, options) {
            const self = this;
            const host = this.openModal(root, fallbackName || 'Playlist', options);
            host.innerHTML = '<div class="jellyspot-empty">Opening playlist…</div>';
            ApiClient.ajax({ type: 'GET', url: ApiClient.getUrl('JellySpot/Playlists/' + encodeURIComponent(id)), dataType: 'json' })
                .then(function (data) {
                    data = self.coercePayload(data) || {};
                    const playlist = self.pick(data, 'Playlist', 'playlist') || {};
                    const tracks = self.coerceTracks(self.pick(data, 'Tracks', 'tracks'));
                    const name = self.pick(playlist, 'Name', 'name') || fallbackName || 'Playlist';
                    self._modalRestore = function () {
                        self.openPlaylist(root, id, name, { replace: true });
                    };
                    self.renderTrackPicker(root, {
                        kind: 'playlist',
                        popup: true,
                        id: id,
                        title: name,
                        subtitle: (self.pick(playlist, 'TrackCount', 'trackCount') || tracks.length) + ' tracks',
                        image: self.coverOf(playlist),
                        tracks: tracks,
                        restricted: self.pick(data, 'Restricted', 'restricted') || self.pick(playlist, 'ItemsRestricted', 'itemsRestricted'),
                        owned: self.pick(data, 'Owned', 'owned') || self.pick(playlist, 'Owned', 'owned'),
                        ownerName: self.pick(data, 'OwnerName', 'ownerName') || self.pick(playlist, 'OwnerName', 'ownerName'),
                        error: self.pick(data, 'Error', 'error') || self.pick(playlist, 'ItemsError', 'itemsError'),
                        queueAll: function () { self.queueItem('playlist', id, name, root); },
                        monitor: function () {
                            ApiClient.ajax({
                                type: 'POST',
                                url: ApiClient.getUrl('JellySpot/Playlists/' + encodeURIComponent(id) + '/Monitor')
                            }).then(function () {
                                self.notify(root, 'Monitoring ' + name + ' on sync', 'ok');
                            });
                        },
                        playlistId: id,
                        playlistName: name
                    });
                })
                .catch(function () {
                    self.showModalMessage(root, 'Could not open that playlist', 'The request failed. Link Spotify from Sync if this keeps happening.');
                    self.notify(root, 'Could not open that playlist.', 'error');
                });
        },

        openAlbum: function (root, id, fallbackName, options) {
            const self = this;
            const host = this.openModal(root, fallbackName || 'Album', options);
            host.innerHTML = '<div class="jellyspot-empty">Opening album…</div>';
            ApiClient.ajax({ type: 'GET', url: ApiClient.getUrl('JellySpot/Albums/' + encodeURIComponent(id)), dataType: 'json' })
                .then(function (data) {
                    data = self.coercePayload(data) || {};
                    const album = self.pick(data, 'Album', 'album') || {};
                    const tracks = self.coerceTracks(self.pick(data, 'Tracks', 'tracks'));
                    const name = self.pick(album, 'Name', 'name') || fallbackName || 'Album';
                    self._modalRestore = function () {
                        self.openAlbum(root, id, name, { replace: true });
                    };
                    self.renderTrackPicker(root, {
                        kind: 'album',
                        popup: true,
                        id: id,
                        title: name,
                        subtitle: self.pick(album, 'Artists', 'artists') || '',
                        image: self.coverOf(album) || (tracks[0] && self.coverOf(tracks[0])),
                        tracks: tracks,
                        queueAll: function () { self.queueItem('album', id, name, root); },
                        monitor: function () {
                            ApiClient.ajax({
                                type: 'POST',
                                url: ApiClient.getUrl('JellySpot/Albums/' + encodeURIComponent(id) + '/Monitor')
                            }).then(function () {
                                self.notify(root, 'Monitoring ' + name + ' on sync', 'ok');
                            });
                        },
                        playlistId: id,
                        playlistName: name
                    });
                })
                .catch(function () {
                    self.showModalMessage(root, 'Could not open that album', 'Spotify did not return album tracks.');
                    self.notify(root, 'Could not open that album.', 'error');
                });
        },

        queueTrackIds: function (root, ids, playlistId, playlistName) {
            const self = this;
            if (!ids.length) {
                this.notify(root, 'Nothing to queue.', 'ok');
                return;
            }
            this.notify(root, 'Queuing ' + ids.length + ' tracks…', 'info');
            ApiClient.ajax({
                type: 'POST',
                url: ApiClient.getUrl('JellySpot/Queue/Tracks'),
                contentType: 'application/json',
                data: JSON.stringify({
                    TrackIds: ids,
                    PlaylistId: playlistId,
                    PlaylistName: playlistName
                })
            }).then(function (res) {
                self.notify(root, self.formatQueueMessage(res, 'Queued tracks'), 'ok', {
                    label: 'View queue',
                    run: function () { self.showLibrarySection(root, 'queue'); }
                });
            }).catch(function () {
                self.notify(root, 'Queue failed. Check Sync, then try again.', 'error');
            });
        },

        renderTrackPicker: function (root, spec) {
            const self = this;
            const usePopup = spec.popup === true || spec.kind === 'playlist' || spec.kind === 'album';
            const stage = usePopup
                ? this.openModal(root, spec.title, { replace: true })
                : (root.querySelector('.jellyspot-browse-results') || root.querySelector('.jellyspot-liked-results'));
            if (!stage) {
                return;
            }

            const tracks = this.coerceTracks(spec.tracks);
            if (!tracks.length) {
                if (spec.kind === 'playlist') {
                    var owner = spec.ownerName ? ' It belongs to ' + spec.ownerName + '.' : '';
                    var detail = spec.owned
                        ? 'Spotify returned this playlist but no songs. Link Spotify again from Sync so playlist-read-private is granted, and confirm you own or collaborate on it.'
                        : 'This is not your playlist.' + owner + ' Spotify Development Mode only sends songs for playlists you created or collaborate on. Linking again will not unlock followed or public lists like this. Open a playlist you made, or use Liked Songs and albums.';
                    this.showModalMessage(root, spec.title || 'Playlist', detail);
                    this.notify(root, spec.owned ? 'No playlist songs returned' : 'Followed playlists have no track list', 'error');
                    return;
                }
                if (usePopup) {
                    this.showModalMessage(root, 'No playable tracks', 'Spotify returned this ' + (spec.kind || 'item') + ' but none of the tracks were readable.');
                } else {
                    this.showStageMessage(root, 'No playable tracks', 'Spotify returned this ' + (spec.kind || 'item') + ' but none of the tracks were readable.');
                }
                this.notify(root, '0 tracks loaded', 'error');
                return;
            }
            stage.innerHTML =
                '<div class="jellyspot-detail">' +
                '<button type="button" class="jellyspot-text-back jellyspot-back-btn">' + (usePopup ? '← Back' : '← Library') + '</button>' +
                '<div class="jellyspot-detail-head">' +
                (spec.image
                    ? '<img class="jellyspot-detail-cover" src="' + this.escapeHtml(spec.image) + '" alt="" />'
                    : '<div class="jellyspot-detail-fallback"></div>') +
                '<div class="jellyspot-detail-meta">' +
                '<div class="jellyspot-kicker">' + this.escapeHtml(spec.kind) + '</div>' +
                '<h3>' + this.escapeHtml(spec.title) + '</h3>' +
                '<p class="jellyspot-lede">' + this.escapeHtml(spec.subtitle || tracks.length + ' tracks') + '</p>' +
                '<div class="jellyspot-hero-actions">' +
                '<button type="button" class="jellyspot-card-btn is-primary jellyspot-queue-missing">Queue missing</button>' +
                '<button type="button" class="jellyspot-card-btn jellyspot-queue-all">Queue all</button>' +
                (spec.monitor ? '<button type="button" class="jellyspot-card-btn jellyspot-monitor-btn">Monitor on sync</button>' : '') +
                '</div></div></div>' +
                '<div class="jellyspot-actionbar">' +
                '<input type="search" class="jellyspot-track-filter" placeholder="Filter tracks" />' +
                '<button type="button" class="jellyspot-text-btn jellyspot-select-all">Select all</button>' +
                '<button type="button" class="jellyspot-text-btn jellyspot-select-none">Select none</button>' +
                '<button type="button" class="jellyspot-card-btn is-primary jellyspot-queue-selected">Queue selected</button>' +
                '<span class="jellyspot-count"></span>' +
                '</div>' +
                '<table class="jellyspot-track-table"><thead><tr>' +
                '<th class="jellyspot-col-check"></th><th class="jellyspot-col-num">#</th><th>Title</th>' +
                '<th class="jellyspot-col-album">Album</th><th class="jellyspot-col-duration">Time</th>' +
                '<th>Status</th><th></th>' +
                '</tr></thead><tbody></tbody></table>' +
                '</div>';

            const tbody = stage.querySelector('tbody');
            tracks.forEach(function (track, index) {
                const id = self.pick(track, 'Id', 'id');
                const title = self.pick(track, 'Name', 'name') || 'Untitled';
                const artists = self.artistsOf(track);
                const album = self.albumNameOf(track);
                const albumId = self.albumIdOf(track);
                const duration = self.pick(track, 'DurationMs', 'durationMs', 'duration_ms') || 0;
                const image = self.coverOf(track);
                const viewingThisAlbum = spec.kind === 'album' && spec.id && albumId && spec.id === albumId;
                const canOpenAlbum = !!albumId && !viewingThisAlbum;
                const tr = document.createElement('tr');
                tr.dataset.trackId = id;
                tr.dataset.search = (title + ' ' + artists + ' ' + album).toLowerCase();
                tr.innerHTML =
                    '<td class="jellyspot-col-check"><input type="checkbox" class="jellyspot-track-check" checked /></td>' +
                    '<td class="jellyspot-col-num jellyspot-muted">' + (index + 1) + '</td>' +
                    '<td><div class="jellyspot-track-main">' +
                    (image ? '<img src="' + self.escapeHtml(image) + '" alt="" />' : '<div class="jellyspot-track-fallback"></div>') +
                    '<div>' +
                    (canOpenAlbum
                        ? '<button type="button" class="jellyspot-link jellyspot-track-title" data-album-id="' + self.escapeHtml(albumId) + '">' + self.escapeHtml(title) + '</button>'
                        : '<div class="jellyspot-track-title">' + self.escapeHtml(title) + '</div>') +
                    '<div class="jellyspot-muted">' + self.artistLinksHtml(track) + '</div></div></div></td>' +
                    '<td class="jellyspot-col-album">' +
                    (canOpenAlbum
                        ? '<button type="button" class="jellyspot-link" data-album-id="' + self.escapeHtml(albumId) + '">' + self.escapeHtml(album) + '</button>'
                        : '<span class="jellyspot-muted">' + self.escapeHtml(album) + '</span>') +
                    '</td>' +
                    '<td class="jellyspot-col-duration jellyspot-muted">' + self.escapeHtml(self.formatDuration(duration)) + '</td>' +
                    '<td><span class="jellyspot-pill">Ready</span></td>' +
                    '<td><button type="button" class="jellyspot-row-queue">Queue</button></td>';
                tr.querySelectorAll('[data-album-id]').forEach(function (albumBtn) {
                    albumBtn.addEventListener('click', function (e) {
                        e.stopPropagation();
                        self.openAlbum(root, albumBtn.getAttribute('data-album-id'), album || title);
                    });
                });
                tr.querySelector('.jellyspot-row-queue').addEventListener('click', function (e) {
                    e.stopPropagation();
                    self.queueItem('track', id, title, root);
                });
                tr.querySelector('.jellyspot-track-check').addEventListener('change', function () {
                    self.syncTrackSelection(stage);
                });
                tbody.appendChild(tr);
            });
            this.bindArtistLinks(root, tbody);

            const backBtn = stage.querySelector('.jellyspot-back-btn');
            if (backBtn) {
                backBtn.addEventListener('click', function () {
                    if (usePopup) {
                        self.closeModal(root);
                        return;
                    }
                    self.goBrowseBack(root);
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
                self.queueTrackIds(root, self.selectedTrackIds(stage), spec.playlistId || spec.kind, spec.playlistName || spec.title);
            });
            stage.querySelector('.jellyspot-queue-all').addEventListener('click', function () {
                spec.queueAll();
            });
            stage.querySelector('.jellyspot-queue-missing').addEventListener('click', function () {
                const ids = Array.prototype.map.call(stage.querySelectorAll('tr[data-track-id]:not(.is-owned)'), function (row) {
                    return row.classList.contains('is-hidden') ? null : row.dataset.trackId;
                }).filter(Boolean);
                if (!ids.length) {
                    self.notify(root, 'Everything here is already in your library.', 'ok');
                    return;
                }
                self.queueTrackIds(root, ids, spec.playlistId || spec.kind, spec.playlistName || spec.title);
            });
            const monitor = stage.querySelector('.jellyspot-monitor-btn');
            if (monitor && spec.monitor) {
                monitor.addEventListener('click', spec.monitor);
            }

            this.syncTrackSelection(stage);
            this.setBrowseStatus(root, tracks.length + ' tracks');
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

        lookupOwnedIds: function (ids, onDone) {
            const list = (ids || []).filter(Boolean);
            if (!list.length) {
                onDone({});
                return;
            }
            const chunks = [];
            for (let i = 0; i < list.length; i += 80) {
                chunks.push(list.slice(i, i + 80));
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
                onDone(owned);
            }).catch(function () {
                onDone({});
            });
        },

        markOwnedCards: function (root) {
            const cards = root.querySelectorAll('.jellyspot-card[data-track-id]');
            const ids = Array.prototype.map.call(cards, function (card) {
                return card.dataset.trackId;
            });
            this.lookupOwnedIds(ids, function (owned) {
                cards.forEach(function (card) {
                    if (!owned[card.dataset.trackId]) {
                        return;
                    }
                    card.classList.add('is-owned');
                    const queue = card.querySelector('.jellyspot-card-queue');
                    if (queue) {
                        queue.remove();
                    }
                    const art = card.querySelector('.jellyspot-card-art');
                    if (art && !art.querySelector('.jellyspot-owned-badge')) {
                        const badge = document.createElement('span');
                        badge.className = 'jellyspot-owned-badge';
                        badge.textContent = 'In library';
                        art.appendChild(badge);
                    }
                });
            });
        },

        markOwnedTracks: function (stage) {
            const rows = stage.querySelectorAll('tr[data-track-id]');
            const ids = Array.prototype.map.call(rows, function (row) {
                return row.dataset.trackId;
            });
            this.lookupOwnedIds(ids, function (owned) {
                rows.forEach(function (row) {
                    const pill = row.querySelector('.jellyspot-pill');
                    if (!pill) {
                        return;
                    }
                    const isOwned = !!owned[row.dataset.trackId];
                    pill.textContent = isOwned ? 'In library' : 'Ready';
                    pill.classList.toggle('is-owned', isOwned);
                    row.classList.toggle('is-owned', isOwned);
                    const rowQueue = row.querySelector('.jellyspot-row-queue');
                    if (rowQueue) {
                        rowQueue.textContent = isOwned ? 'Saved' : 'Queue';
                        rowQueue.disabled = isOwned;
                    }
                });
            });
        },

        setLibraryNav: function (root, section) {
            const tab = (section === 'playlists' || section === 'albums' || section === 'artists'
                || section === 'recent' || section === 'top-tracks' || section === 'top-artists'
                || section === 'home')
                ? 'home'
                : section;
            root.querySelectorAll('.jellyspot-page-tab').forEach(function (btn) {
                btn.classList.toggle('is-active', btn.getAttribute('data-lib') === tab);
            });
        },

        applyPendingInner: function (root) {
            const inner = this._pendingInner;
            this._pendingInner = null;
            if (!inner || !this.INNER_SECTIONS[inner]) {
                return false;
            }
            this.showLibrarySection(root, inner);
            return true;
        },

        showLibraryChrome: function (root, id) {
            const isAccount = id === 'sync' || id === 'queue';
            const library = root.querySelector('.jellyspot-library-chrome');
            const syncPane = root.querySelector('[data-pane="sync"]');
            const queuePane = root.querySelector('[data-pane="queue"]');
            if (library) {
                library.classList.toggle('hide', isAccount);
            }
            if (syncPane) {
                syncPane.classList.toggle('hide', id !== 'sync');
            }
            if (queuePane) {
                queuePane.classList.toggle('hide', id !== 'queue');
            }
        },

        showLibrarySection: function (root, section) {
            const self = this;
            const id = section || 'home';
            this.closeModal(root, true);
            this._currentLib = id;
            this._browseBack = function () {
                self.showLibrarySection(root, 'home');
            };
            this.setLibraryNav(root, id);
            this.showLibraryChrome(root, id);
            if (id === 'sync') {
                const pane = root.querySelector('[data-pane="sync"]');
                if (pane) {
                    this.renderSync(pane);
                }
            } else if (id === 'queue') {
                const pane = root.querySelector('[data-pane="queue"]');
                if (pane) {
                    this.renderQueue(pane);
                }
            } else if (id === 'home') {
                this.loadLibraryHome(root);
            } else if (id === 'playlists') {
                this.loadPlaylists(root);
            } else if (id === 'albums') {
                this.loadSavedAlbums(root);
            } else if (id === 'artists') {
                this.loadFollowedArtists(root);
            } else if (id === 'recent') {
                this.loadRecentlyPlayed(root);
            } else if (id === 'top-tracks') {
                this.loadTopTracks(root);
            } else if (id === 'top-artists') {
                this.loadTopArtists(root);
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

        previewItems: function (list) {
            return (list || []).slice(0, this.SHELF_PREVIEW);
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
            row.className = 'jellyspot-grid';
            renderCards(row);
            if (!row.children.length) {
                const empty = document.createElement('div');
                empty.className = 'jellyspot-empty is-inline';
                empty.textContent = 'Nothing here yet.';
                row.appendChild(empty);
            }
            stage.appendChild(row);
        },

        startLibraryPage: function (root) {
            const self = this;
            const stage = root.querySelector('.jellyspot-browse-results');
            stage.innerHTML = '';
            this.insertLibraryBack(root, stage, function () {
                self.clearSearch(root);
                self.showLibrarySection(root, 'home');
            });
            return stage;
        },

        addRangeBar: function (stage, current, onPick) {
            const bar = document.createElement('div');
            bar.className = 'jellyspot-range-bar';
            [
                ['short_term', '4 weeks'],
                ['medium_term', '6 months'],
                ['long_term', '1 year']
            ].forEach(function (pair) {
                const btn = document.createElement('button');
                btn.type = 'button';
                btn.className = 'jellyspot-range-btn' + (pair[0] === current ? ' is-active' : '');
                btn.textContent = pair[1];
                btn.addEventListener('click', function () {
                    onPick(pair[0]);
                });
                bar.appendChild(btn);
            });
            stage.appendChild(bar);
            return bar;
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
                    const likedCover = liked[0] ? self.coverOf(liked[0]) : '';

                    const featured = document.createElement('button');
                    featured.type = 'button';
                    featured.className = 'jellyspot-featured';
                    featured.innerHTML =
                        (likedCover ? '<img src="' + self.escapeHtml(likedCover) + '" alt="" />' : '<div class="jellyspot-card-fallback"></div>') +
                        '<div class="jellyspot-featured-copy">' +
                        '<div class="jellyspot-kicker">Playlist</div>' +
                        '<h3>Liked Songs</h3>' +
                        '<p>' + likedCount + ' liked tracks on this account</p>' +
                        '<span class="jellyspot-card-btn is-primary">Open</span>' +
                        '</div>';
                    featured.addEventListener('click', function () {
                        self.showLibrarySection(root, 'liked');
                    });
                    stage.appendChild(featured);

                    if (self.pick(data, 'NeedsRelink', 'needsRelink')) {
                        const relink = document.createElement('div');
                        relink.className = 'jellyspot-empty is-inline';
                        relink.innerHTML = '<strong>Link Spotify again</strong><p>Recently played and Your top tracks need new Spotify permission. Open Sync and click Link Spotify.</p>';
                        stage.appendChild(relink);
                    }

                    function viewAll(section) {
                        return function () { self.showLibrarySection(root, section); };
                    }

                    function addTrackShelf(title, list, total, section) {
                        list = list || [];
                        if (!list.length) {
                            return;
                        }
                        const count = total || list.length;
                        self.addShelf(stage, title, count, viewAll(section), function (row) {
                            self.previewItems(list).forEach(function (track) {
                                self.addTrackCard(row, root, track);
                            });
                        });
                    }

                    addTrackShelf(
                        'Recently played',
                        self.pick(data, 'RecentlyPlayed', 'recentlyPlayed'),
                        self.pick(data, 'RecentlyPlayedCount', 'recentlyPlayedCount'),
                        'recent'
                    );
                    addTrackShelf(
                        'Your top tracks',
                        self.pick(data, 'TopTracks', 'topTracks'),
                        self.pick(data, 'TopTrackCount', 'topTrackCount'),
                        'top-tracks'
                    );

                    const topArtists = self.pick(data, 'TopArtists', 'topArtists') || [];
                    const topArtistCount = self.pick(data, 'TopArtistCount', 'topArtistCount') || topArtists.length;
                    if (topArtists.length) {
                        self.addShelf(stage, 'Your top artists', topArtistCount, viewAll('top-artists'), function (row) {
                            self.previewItems(topArtists).forEach(function (artist) {
                                self.addArtistCard(row, root, artist);
                            });
                        });
                    }

                    self.addShelf(stage, 'Liked songs', likedCount, function () {
                        self.showLibrarySection(root, 'liked');
                    }, function (row) {
                        self.previewItems(liked).forEach(function (track) {
                            self.addTrackCard(row, root, track);
                        });
                    });

                    const playlistCount = self.pick(data, 'PlaylistCount', 'playlistCount') || playlists.length;
                    self.addShelf(stage, 'Playlists', playlistCount, viewAll('playlists'), function (row) {
                        self.previewItems(playlists).forEach(function (p) {
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

                    const albumCount = self.pick(data, 'AlbumCount', 'albumCount') || albums.length;
                    self.addShelf(stage, 'Saved albums', albumCount, viewAll('albums'), function (row) {
                        self.previewItems(albums).forEach(function (album) {
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

                    const artistCount = self.pick(data, 'ArtistCount', 'artistCount') || artists.length;
                    self.addShelf(stage, 'Artists you follow', artistCount, viewAll('artists'), function (row) {
                        self.previewItems(artists).forEach(function (artist) {
                            self.addArtistCard(row, root, artist);
                        });
                    });

                    self.setBrowseStatus(root, 'Your library — open a playlist or album to pick tracks');
                    self.markOwnedCards(stage);
                })
                .catch(function () {
                    self.setBrowseStatus(root, 'Could not load your library.');
                });
        },

        loadRecentlyPlayed: function (root) {
            const self = this;
            this.setLibraryNav(root, 'recent');
            this.setBrowseStatus(root, 'Loading recently played…');
            ApiClient.ajax({ type: 'GET', url: ApiClient.getUrl('JellySpot/Library/RecentlyPlayed'), dataType: 'json' })
                .then(function (tracks) {
                    tracks = self.coerceTracks(self.coercePayload(tracks)) || tracks || [];
                    const stage = self.startLibraryPage(root);
                    const list = Array.isArray(tracks) ? tracks : [];
                    if (!list.length) {
                        const empty = document.createElement('div');
                        empty.className = 'jellyspot-empty';
                        empty.textContent = 'No recently played tracks on this account.';
                        stage.appendChild(empty);
                        self.setBrowseStatus(root, 'No recently played');
                        return;
                    }
                    const grid = document.createElement('div');
                    grid.className = 'jellyspot-grid';
                    list.forEach(function (track) {
                        self.addTrackCard(grid, root, track);
                    });
                    stage.appendChild(grid);
                    self.setBrowseStatus(root, list.length + ' recently played');
                    self.markOwnedCards(stage);
                })
                .catch(function () {
                    self.setBrowseStatus(root, 'Could not load recently played.');
                });
        },

        loadTopTracks: function (root, range) {
            const self = this;
            range = range || this._topTrackRange || 'medium_term';
            this._topTrackRange = range;
            this.setLibraryNav(root, 'top-tracks');
            this.setBrowseStatus(root, 'Loading your top tracks…');
            ApiClient.ajax({
                type: 'GET',
                url: ApiClient.getUrl('JellySpot/Library/TopTracks?range=' + encodeURIComponent(range)),
                dataType: 'json'
            })
                .then(function (tracks) {
                    tracks = self.coerceTracks(self.coercePayload(tracks)) || tracks || [];
                    const stage = self.startLibraryPage(root);
                    self.addRangeBar(stage, range, function (next) {
                        self.loadTopTracks(root, next);
                    });
                    const list = Array.isArray(tracks) ? tracks : [];
                    if (!list.length) {
                        const empty = document.createElement('div');
                        empty.className = 'jellyspot-empty';
                        empty.textContent = 'No top tracks yet. Link Spotify again from Sync if this stays empty.';
                        stage.appendChild(empty);
                        self.setBrowseStatus(root, 'No top tracks');
                        return;
                    }
                    const grid = document.createElement('div');
                    grid.className = 'jellyspot-grid';
                    list.forEach(function (track) {
                        self.addTrackCard(grid, root, track);
                    });
                    stage.appendChild(grid);
                    self.setBrowseStatus(root, list.length + ' top tracks');
                    self.markOwnedCards(stage);
                })
                .catch(function () {
                    self.setBrowseStatus(root, 'Could not load top tracks.');
                });
        },

        loadTopArtists: function (root, range) {
            const self = this;
            range = range || this._topArtistRange || 'medium_term';
            this._topArtistRange = range;
            this.setLibraryNav(root, 'top-artists');
            this.setBrowseStatus(root, 'Loading your top artists…');
            ApiClient.ajax({
                type: 'GET',
                url: ApiClient.getUrl('JellySpot/Library/TopArtists?range=' + encodeURIComponent(range)),
                dataType: 'json'
            })
                .then(function (artists) {
                    artists = self.coercePayload(artists);
                    const stage = self.startLibraryPage(root);
                    self.addRangeBar(stage, range, function (next) {
                        self.loadTopArtists(root, next);
                    });
                    const list = Array.isArray(artists) ? artists : [];
                    if (!list.length) {
                        const empty = document.createElement('div');
                        empty.className = 'jellyspot-empty';
                        empty.textContent = 'No top artists yet. Link Spotify again from Sync if this stays empty.';
                        stage.appendChild(empty);
                        self.setBrowseStatus(root, 'No top artists');
                        return;
                    }
                    const grid = document.createElement('div');
                    grid.className = 'jellyspot-grid';
                    list.forEach(function (artist) {
                        self.addArtistCard(grid, root, artist);
                    });
                    stage.appendChild(grid);
                    self.setBrowseStatus(root, list.length + ' top artists');
                })
                .catch(function () {
                    self.setBrowseStatus(root, 'Could not load top artists.');
                });
        },

        loadSavedAlbums: function (root) {
            const self = this;
            this.setLibraryNav(root, 'albums');
            this.setBrowseStatus(root, 'Loading saved albums…');
            ApiClient.ajax({ type: 'GET', url: ApiClient.getUrl('JellySpot/Library/Albums'), dataType: 'json' })
                .then(function (albums) {
                    albums = self.coerceTracks(self.coercePayload(albums)) || albums || [];
                    const stage = self.startLibraryPage(root);
                    const list = Array.isArray(albums) ? albums : [];
                    if (!list.length) {
                        const empty = document.createElement('div');
                        empty.className = 'jellyspot-empty';
                        empty.textContent = 'No saved albums on this Spotify account.';
                        stage.appendChild(empty);
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
                    const stage = self.startLibraryPage(root);
                    const list = Array.isArray(artists) ? artists : [];
                    if (!list.length) {
                        const empty = document.createElement('div');
                        empty.className = 'jellyspot-empty';
                        empty.textContent = 'You are not following any artists yet.';
                        stage.appendChild(empty);
                        self.setBrowseStatus(root, 'No followed artists');
                        return;
                    }
                    const grid = document.createElement('div');
                    grid.className = 'jellyspot-grid';
                    list.forEach(function (artist) {
                        self.addArtistCard(grid, root, artist);
                    });
                    stage.appendChild(grid);
                    self.setBrowseStatus(root, list.length + ' artists');
                })
                .catch(function () {
                    self.setBrowseStatus(root, 'Could not load artists.');
                });
        },

        openArtist: function (root, id, fallbackName, options) {
            const self = this;
            const stage = this.openModal(root, fallbackName || 'Artist', options);
            stage.innerHTML = '<div class="jellyspot-empty">Opening artist…</div>';
            ApiClient.ajax({ type: 'GET', url: ApiClient.getUrl('JellySpot/Artists/' + encodeURIComponent(id)), dataType: 'json' })
                .then(function (data) {
                    data = self.coercePayload(data) || {};
                    const artist = self.pick(data, 'Artist', 'artist') || {};
                    const albums = self.pick(data, 'Albums', 'albums') || [];
                    const topTracks = self.pick(data, 'TopTracks', 'topTracks') || [];
                    const name = self.pick(artist, 'Name', 'name') || fallbackName || 'Artist';
                    const genres = self.pick(artist, 'Genres', 'genres') || [];
                    const genreText = Array.isArray(genres) ? genres.slice(0, 4).join(' · ') : '';
                    const followers = self.pick(artist, 'Followers', 'followers') || 0;
                    self._modalRestore = function () {
                        self.openArtist(root, id, name, { replace: true });
                    };
                    stage.innerHTML =
                        '<div class="jellyspot-detail">' +
                        '<button type="button" class="jellyspot-text-back jellyspot-artist-back">← Back</button>' +
                        '<div class="jellyspot-detail-head is-artist">' +
                        (self.coverOf(artist)
                            ? '<img class="jellyspot-detail-cover is-round" src="' + self.escapeHtml(self.coverOf(artist)) + '" alt="" />'
                            : '<div class="jellyspot-detail-fallback is-round"></div>') +
                        '<div class="jellyspot-detail-meta">' +
                        '<div class="jellyspot-kicker">artist</div>' +
                        '<h3>' + self.escapeHtml(name) + '</h3>' +
                        '<p class="jellyspot-lede">' + self.escapeHtml(genreText || (followers ? followers.toLocaleString() + ' followers' : 'Artist')) + '</p>' +
                        '</div></div></div>';
                    const artistBack = stage.querySelector('.jellyspot-artist-back');
                    if (artistBack) {
                        artistBack.addEventListener('click', function () {
                            self.closeModal(root);
                        });
                    }
                    if (topTracks.length) {
                        self.addShelf(stage, 'Popular', topTracks.length, null, function (row) {
                            topTracks.slice(0, 10).forEach(function (track) {
                                self.addTrackCard(row, root, track);
                            });
                        });
                    }
                    const discog = document.createElement('div');
                    discog.className = 'jellyspot-section-head';
                    discog.innerHTML = '<h3>Discography</h3><span class="jellyspot-muted">' + albums.length + '</span>';
                    stage.appendChild(discog);
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
                    self.markOwnedCards(stage);
                    self.setBrowseStatus(root, name);
                })
                .catch(function () {
                    self.showModalMessage(root, 'Could not open that artist', 'The artist page failed to load.');
                    self.notify(root, 'Could not open that artist.', 'error');
                });
        },

        loadPlaylists: function (root) {
            const self = this;
            this.setLibraryNav(root, 'playlists');
            this.setBrowseStatus(root, 'Loading playlists…');
            ApiClient.ajax({ type: 'GET', url: ApiClient.getUrl('JellySpot/Playlists'), dataType: 'json' })
                .then(function (playlists) {
                    const stage = self.startLibraryPage(root);
                    const list = playlists || [];
                    if (!list.length) {
                        const empty = document.createElement('div');
                        empty.className = 'jellyspot-empty';
                        empty.textContent = 'No playlists you own or collaborate on. Followed and public lists are hidden because Spotify will not send their songs.';
                        stage.appendChild(empty);
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
                    '<p class="jellyspot-lede">Link your Spotify account. Other Jellyfin users keep their own links and playlists. Link again after this update to load Recently played and Your top tracks.</p>' +
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
                    '<input type="search" class="jellyspot-playlist-filter" placeholder="Filter playlists" />' +
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
                root.querySelector('.jellyspot-playlist-filter').addEventListener('input', function () {
                    self.filterMonitoredPlaylists(root);
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
                self.filterMonitoredPlaylists(root);
            }).catch(function (err) {
                root.querySelector('.jellyspot-link-status').textContent =
                    'Unable to load status. Set Spotify Client ID under Dashboard → JellySpot. ' +
                    (err && err.message ? err.message : '');
            });
        },

        filterMonitoredPlaylists: function (root) {
            const filter = root.querySelector('.jellyspot-playlist-filter');
            const needle = ((filter && filter.value) || '').trim().toLowerCase();
            root.querySelectorAll('.jellyspot-monitored label').forEach(function (label) {
                const text = (label.textContent || '').toLowerCase();
                label.style.display = (!needle || text.indexOf(needle) >= 0) ? 'block' : 'none';
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
                items = items || [];
                const tbody = root.querySelector('.jellyspot-queue-body');
                tbody.innerHTML = '';
                if (!items.length) {
                    tbody.innerHTML = '<tr><td colspan="5">Nothing in the queue.</td></tr>';
                    self.armQueuePoll(root, false);
                    return;
                }
                let active = false;
                items.forEach(function (item) {
                    const tr = document.createElement('tr');
                    const title = self.pick(item, 'Title', 'title') || '';
                    const artists = self.pick(item, 'Artists', 'artists') || '';
                    const st = self.pick(item, 'Status', 'status') || '';
                    const score = self.pick(item, 'MatchScore', 'matchScore');
                    const detail = self.pick(item, 'Error', 'error') || self.pick(item, 'YoutubeVideoId', 'youtubeVideoId') || '';
                    const id = self.pick(item, 'Id', 'id');
                    if (st === 'Pending' || st === 'Matching' || st === 'Downloading') {
                        active = true;
                    }
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
                self.armQueuePoll(root, active);
            }).catch(function () {
                root.querySelector('.jellyspot-queue-body').innerHTML =
                    '<tr><td colspan="5">Could not load queue</td></tr>';
                self.armQueuePoll(root, false);
            });
        },

        armQueuePoll: function (root, active) {
            const self = this;
            if (this._queuePollTimer) {
                clearTimeout(this._queuePollTimer);
                this._queuePollTimer = null;
            }
            if (!active || this._currentLib !== 'queue') {
                return;
            }
            this._queuePollTimer = setTimeout(function () {
                if (self._currentLib === 'queue') {
                    self.loadQueue(root);
                }
            }, 4000);
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

