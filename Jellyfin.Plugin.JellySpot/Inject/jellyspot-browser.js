/* Local-first browser. Spotify work is persisted and processed outside HTTP requests. */
(function () {
  "use strict";
  const plugin = window.jellySpotPlugin;
  const norm = (v) =>
    Array.isArray(v)
      ? v.map(norm)
      : v && typeof v === "object"
        ? Object.fromEntries(
            Object.entries(v).map(([k, x]) => [
              k[0].toLowerCase() + k.slice(1),
              norm(x),
            ]),
          )
        : v;
  const el = (tag, cls, text) => {
    const n = document.createElement(tag);
    if (cls) {
      n.className = cls;
      if (cls.includes("material-icons") || cls.includes("jb-art-placeholder"))
        n.setAttribute("aria-hidden", "true");
    }
    if (text != null) n.textContent = text;
    return n;
  };
  const date = (v) =>
    v && !Number.isNaN(new Date(v).getTime())
      ? new Date(v).toLocaleDateString(undefined, {
          year: "numeric",
          month: "short",
          day: "numeric",
        })
      : "Unknown";
  const exact = (v) => (v ? new Date(v).toLocaleString() : "Unknown");
  const duration = (ms) =>
    `${Math.floor((ms || 0) / 60000)}:${String(Math.floor(((ms || 0) % 60000) / 1000)).padStart(2, "0")}`;
  const labels = {
    overview: "Overview",
    mine: "My Playlists",
    playlists: "Spotify Playlists",
    liked: "Liked Songs",
    albums: "Albums",
    artists: "Artists",
    shared: "Shared Playlists",
    following: "Following",
    downloads: "Downloads",
    history: "Sync History",
    account: "Account",
    recent: "Recently Played",
    "top-tracks": "Top Tracks",
    "top-artists": "Top Artists",
  };
  class Browser {
    constructor(root) {
      this.root = root;
      this.identity = plugin.collectionIdentity();
      this.cache = new Map();
      this.selections = new Map();
      this.pending = new Map();
      this.operationKeys = new Map();
      this.epoch = 0;
      this.route = { view: "overview" };
      this.pageState = new Map();
      this.disposed = false;
      root.dataset.jellyspotMounted = "true";
      root.classList.add("jb-host");
      root.innerHTML = "";
      this.shell = el("div", "jb-app");
      root.appendChild(this.shell);
      const header = el("header", "jb-header"),
        brand = el("div", "jb-brand");
      brand.append(
        el("span", "material-icons", "graphic_eq"),
        el("h2", "", "JellySpot"),
      );
      header.appendChild(brand);
      const search = el("form", "jb-search");
      this.search = el("input");
      this.search.type = "search";
      this.search.placeholder = "Search Spotify";
      this.search.setAttribute("aria-label", "Search Spotify");
      search.append(
        this.search,
        this.button("Search", () => search.requestSubmit(), "jb-icon-button"),
      );
      search.onsubmit = (e) => {
        e.preventDefault();
        if (this.search.value.trim())
          this.navigate({
            view: "search",
            q: this.search.value.trim(),
            page: 1,
          });
      };
      header.appendChild(search);
      this.account = this.button(
        "Account",
        () => this.navigate({ view: "account" }),
        "jb-account-chip",
      );
      header.appendChild(this.account);
      this.shell.appendChild(header);
      this.nav = el("nav", "jb-nav");
      this.nav.setAttribute("aria-label", "JellySpot navigation");
      for (const [view, title, icon] of [
        ["overview", "Overview", "space_dashboard"],
        ["mine", "Library", "library_music"],
        ["downloads", "Activity", "sync"],
        ["account", "Account", "person"],
      ]) {
        const b = this.button(
          title,
          () => this.navigate({ view }),
          "jb-nav-button",
        );
        b.dataset.view = view;
        b.prepend(el("span", "material-icons", icon));
        this.nav.appendChild(b);
      }
      this.shell.appendChild(this.nav);
      this.live = this.button(
        "Loading activity…",
        () => this.navigate({ view: this.activity?.operations?.some(o => ["Queued", "Running", "Failed", "PartiallyFailed"].includes(o.status)) ? "history" : "downloads" }),
        "jb-live",
      );
      this.live.setAttribute("aria-live", "polite");
      this.shell.appendChild(this.live);
      this.notifications = el("div", "jb-notifications");
      this.notifications.setAttribute("role", "status");
      this.notifications.setAttribute("aria-live", "polite");
      this.shell.appendChild(this.notifications);
      this.main = el("main", "jb-main");
      this.main.tabIndex = -1;
      this.shell.appendChild(this.main);
      this.onPop = () => {
        if (this.current()) this.navigate(this.routeFromUrl(), false);
      };
      this.onVisibility = () => {
        clearTimeout(this.timer);
        if (!document.hidden && this.current()) this.poll();
      };
      window.addEventListener("popstate", this.onPop);
      document.addEventListener("visibilitychange", this.onVisibility);
      this.api("Me")
        .then((me) => {
          if (!this.current()) return;
          this.me = me;
          this.account.textContent = me.linked
            ? me.spotifyDisplayName || "My account"
            : "Link Spotify";
          this.navigate(this.routeFromUrl(), false);
          if (me.linked) this.ensureIndex();
        })
        .catch(() =>
          this.message("Could not load your account. Refresh to retry.", true),
        );
      this.navigate(this.routeFromUrl(), false);
      this.poll();
    }
    current() {
      return (
        !this.disposed &&
        this.root.isConnected &&
        this.identity === plugin.collectionIdentity()
      );
    }
    dispose() {
      this.disposed = true;
      clearTimeout(this.timer);
      window.removeEventListener("popstate", this.onPop);
      document.removeEventListener("visibilitychange", this.onVisibility);
      this.cache.clear();
      this.selections.clear();
    }
    button(text, action, cls = "jb-button") {
      const b = el("button", cls, text);
      b.type = "button";
      b.onclick = async (e) => {
        if (b.disabled) return;
        try {
          await action(e);
        } catch (error) {
          if (this.current())
            this.message(error.message || "Action failed. Try again.", true);
        }
      };
      return b;
    }
    async api(path, method = "GET", body) {
      const cached = this.cache.get(path),
        options = {
          type: method,
          url: ApiClient.getUrl("JellySpot/" + path),
          dataType: method === "GET" ? "json" : undefined,
          contentType: "application/json",
        };
      if (body != null) options.data = JSON.stringify(body);
      if (method === "GET" && cached?.revision) {
        options.headers = { "If-None-Match": '"' + cached.revision + '"' };
        // Jellyfin's SDK otherwise tries to parse an empty 304 body as JSON.
        options.dataType = undefined;
      }
      let response;
      try {
        response = await plugin.apiRequest(options);
        if (response?.status === 304) response = cached;
        else if (typeof response?.json === "function") response = await response.json();
      } catch (error) {
        if (error?.status === 304 && cached) return cached;
        throw new Error(
          error?.responseJSON?.error ||
            error?.responseJSON?.Error ||
            "Could not complete the request. Check your connection and try again.",
        );
      }
      if (!this.current()) throw new Error("Account changed.");
      const value =
        response == null && method === "GET" ? cached : norm(response);
      if (method === "GET" && value != null) this.cache.set(path, value);
      return value;
    }
    message(text, error = false) {
      this.notifications.textContent = text;
      this.notifications.classList.toggle("is-error", error);
    }
    routeFromUrl() {
      try {
        return JSON.parse(
          new URLSearchParams(location.hash.split("?")[1] || "").get("jsp") ||
            '{"view":"overview"}',
        );
      } catch (_) {
        return { view: "overview" };
      }
    }
    key() {
      return this.route.collectionId
        ? "collection:" + this.route.collectionId
        : this.route.kind
          ? this.route.kind + ":" + this.route.id
          : this.route.view;
    }
    pref(defaults) {
      try {
        return {
          ...defaults,
          ...JSON.parse(
            localStorage.getItem(
              "jellyspot:" + this.identity + ":" + this.key(),
            ) || "{}",
          ),
        };
      } catch (_) {
        return { ...defaults };
      }
    }
    remember(value) {
      try {
        localStorage.setItem(
          "jellyspot:" + this.identity + ":" + this.key(),
          JSON.stringify(value),
        );
      } catch (_) {}
    }
    navigate(route, push = true) {
      if (!this.current()) return;
      const active = document.activeElement,
        focus =
          !push && this.main.contains(active)
            ? {
                label: active.getAttribute("aria-label"),
                start: active.selectionStart,
                end: active.selectionEnd,
              }
            : null;
      this.pageState.set(this.key(), { scroll: window.scrollY });
      this.route = { ...route };
      const epoch = ++this.epoch;
      if (push) {
        const parts = location.hash.split("?"),
          q = new URLSearchParams(parts[1] || "");
        q.set("jsp", JSON.stringify(route));
        history.pushState(
          { ...history.state, jellyspot: route },
          "",
          parts[0] + "?" + q,
        );
      }
      const primary = ["downloads", "history"].includes(route.view)
        ? "downloads"
        : route.view === "account"
          ? "account"
          : route.view === "overview"
            ? "overview"
            : "mine";
      this.nav.querySelectorAll("button").forEach((b) => {
        b.classList.toggle("is-active", b.dataset.view === primary);
        b.setAttribute(
          "aria-current",
          b.dataset.view === primary ? "page" : "false",
        );
      });
      this.main.innerHTML = "";
      this.main.setAttribute("aria-busy", "true");
      Promise.resolve(this.render(epoch))
        .catch((error) => {
          if (epoch === this.epoch && this.current())
            this.errorPanel(error.message, () =>
              this.navigate(this.route, false),
            );
        })
        .finally(() => {
          if (epoch === this.epoch) {
            this.main.setAttribute("aria-busy", "false");
            if (push) this.main.focus({ preventScroll: true });
            const state = this.pageState.get(this.key());
            if (state) window.scrollTo(0, state.scroll);
            if (focus?.label) {
              const target = [
                ...this.main.querySelectorAll("[aria-label]"),
              ].find((n) => n.getAttribute("aria-label") === focus.label);
              if (target) {
                target.focus({ preventScroll: true });
                if (target.setSelectionRange && focus.start != null)
                  target.setSelectionRange(focus.start, focus.end);
              }
            }
          }
        });
    }
    heading(title, subtitle) {
      const h = el("div", "jb-view-head"),
        copy = el("div");
      copy.appendChild(el("h1", "", title));
      if (subtitle) copy.appendChild(el("p", "jb-muted", subtitle));
      h.appendChild(copy);
      this.main.appendChild(h);
      return h;
    }
    errorPanel(text, retry) {
      const p = el("div", "jb-empty");
      p.append(
        el("h3", "", "Something needs attention"),
        el("p", "", text),
        this.button("Retry", retry),
      );
      this.main.appendChild(p);
    }
    loading(host = this.main) {
      const n = el("div", "jb-skeleton");
      n.setAttribute("aria-label", "Loading");
      for (let i = 0; i < 4; i++) n.appendChild(el("div", "jb-skeleton-line"));
      host.appendChild(n);
      return n;
    }
    subnav(items) {
      const n = el("nav", "jb-subnav");
      n.setAttribute("aria-label", "Section navigation");
      for (const view of items) {
        const b = this.button(labels[view], () => this.navigate({ view }));
        b.classList.toggle("is-active", this.route.view === view);
        n.appendChild(b);
      }
      this.main.appendChild(n);
    }
    async render(epoch) {
      const v = this.route.view;
      if (v === "account") return this.renderAccount(epoch);
      if (v === "overview") return this.renderOverview(epoch);
      if (v === "downloads") return this.renderDownloads(epoch);
      if (v === "history") return this.renderHistory(epoch);
      if (v === "search") return this.renderSearch(epoch);
      if (v === "detail") return this.renderDetail(epoch);
      return this.renderLibrary(epoch);
    }
    async poll() {
      if (this.polling) {
        this.pollAgain = true;
        return;
      }
      this.polling = true;
      try {
        clearTimeout(this.timer);
        if (!this.current()) {
          this.dispose();
          return;
        }
        if (document.hidden) return;
        try {
          const data = await this.api("Activity");
          if (!this.current()) return;
          const previous = JSON.stringify(this.activity);
          this.activity = data;
          const active = (data.operations || []).find((o) =>
              ["Queued", "Running"].includes(o.status),
            ),
            counts = data.counts || {},
            downloads =
              (counts.pending || 0) +
              (counts.matching || 0) +
              (counts.downloading || 0);
          const text = active
            ? `${active.kind === "sync" ? "Sync" : "Activity"}: ${active.stage}${active.currentCollection ? " · " + active.currentCollection : ""} · ${active.processed || 0}${active.totalKnown ? " / " + active.discovered : " processed"}`
            : `${downloads} active downloads · ${counts.failed || 0} need attention${data.paused ? " · Your downloads are paused" : ""}`;
          const summary = text + " · Last successful sync: " + date(data.lastSuccessfulSyncUtc);
          if (this.live.textContent !== summary) this.live.textContent = summary;
          if (previous !== JSON.stringify(data)) {
            if (this.route.view === "downloads" && this.refreshDownloads)
              await this.refreshDownloads();
            if (this.route.view === "history" && this.refreshHistory)
              await this.refreshHistory();
            if (
              this.indexing &&
              !data.operations.some(
                (o) =>
                  ["refresh", "inspect-playlist", "inspect-album"].includes(
                    o.kind,
                  ) && ["Queued", "Running"].includes(o.status),
              )
            ) {
              this.indexing = false;
              if (
                [
                  "overview",
                  "detail",
                  "playlists",
                  "albums",
                  "artists",
                  "liked",
                ].includes(this.route.view)
              )
                this.navigate(this.route, false);
            }
          }
          this.timer = setTimeout(
            () => this.poll(),
            active || downloads ? 2000 : 15000,
          );
        } catch (_) {
          if (this.current()) {
            this.live.textContent = "Activity unavailable · reconnecting";
            this.timer = setTimeout(() => this.poll(), 15000);
          }
        }
      } finally {
        this.polling = false;
        if (this.pollAgain && this.current()) {
          this.pollAgain = false;
          clearTimeout(this.timer);
          this.timer = setTimeout(() => this.poll(), 250);
        }
      }
    }
    async operation(input, button) {
      const key = JSON.stringify(input);
      if (this.pending.has(key)) return this.pending.get(key);
      if (button) {
        button.disabled = true;
        button.setAttribute("aria-busy", "true");
      }
      this.message("Preparing request…");
      if (!this.operationKeys.has(key)) this.operationKeys.set(key, window.crypto?.randomUUID?.() || Date.now() + ":" + Math.random());
      const promise = this.api("Operations", "POST", {
        ...input,
        idempotencyKey: this.operationKeys.get(key),
      })
        .then((result) => {
          this.operationKeys.delete(key);
          this.message("Request accepted. Progress is available in Activity.");
          this.poll();
          return result;
        })
        .finally(() => {
          this.pending.delete(key);
          if (button) {
            button.disabled = false;
            button.removeAttribute("aria-busy");
          }
        });
      this.pending.set(key, promise);
      return promise;
    }
    async ensureIndex() {
      try {
        const result = await this.api("Browser/Spotify/playlists?pageSize=1");
        if (!result.complete) {
          this.indexing = true;
          await this.operation({ kind: "refresh" });
        }
      } catch (_) {}
    }
    art(url) {
      if (!url) return el("div", "jb-art jb-art-placeholder", "♫");
      const n = el("img", "jb-art");
      n.alt = "";
      n.loading = "lazy";
      n.decoding = "async";
      n.src = url;
      n.onerror = () =>
        n.replaceWith(el("div", "jb-art jb-art-placeholder", "♫"));
      return n;
    }
    card(item, view) {
      const card = el("article", "jb-card"),
        b = this.button("", () => this.openItem(item, view), "jb-card-open");
      b.append(
        this.art(item.coverUrl || item.imageUrl),
        el("strong", "", item.name || "Untitled"),
      );
      card.appendChild(b);
      card.appendChild(
        el(
          "p",
          "jb-muted",
          item.ownerName
            ? `${item.ownerName} · ${item.trackCount ?? 0} tracks`
            : Array.isArray(item.artists)
              ? item.artists
                  .map((a) => (typeof a === "string" ? a : a.name))
                  .join(", ")
              : "",
        ),
      );
      if (item.owned != null) {
        const badges = el("div", "jb-badges");
        badges.appendChild(
          el("span", "jb-badge", item.shared ? "Shared" : "Private"),
        );
        if (item.monitored)
          badges.appendChild(el("span", "jb-badge", "Kept in sync"));
        if (item.following)
          badges.appendChild(el("span", "jb-badge", "Following"));
        card.appendChild(badges);
      }
      const label =
          view === "following"
            ? "Followed"
            : view === "shared"
              ? "Shared"
              : item.owned != null
                ? "Imported"
                : "Added to Spotify",
        stamp =
          view === "following"
            ? item.followedAtUtc
            : view === "shared"
              ? item.sharedAtUtc
              : item.owned != null
                ? item.importedAtUtc
                : item.addedAtUtc;
      if (item.owned != null || view === "albums") {
        const time = el("time", "jb-date", label + " " + date(stamp));
        time.title = exact(stamp);
        card.appendChild(time);
      }
      return card;
    }
    openItem(item, view) {
      if (["mine", "shared", "following"].includes(view))
        return this.navigate({
          view: "detail",
          collectionId: item.id,
          name: item.name,
          back: view,
        });
      if (view === "albums" || item.type === "album")
        return this.navigate({
          view: "detail",
          kind: "album",
          id: item.id,
          name: item.name,
          back: view,
        });
      if (view === "playlists" || item.type === "playlist")
        return this.navigate({
          view: "detail",
          kind: "playlist",
          id: item.id,
          name: item.name,
          back: view,
        });
      if (view === "artists" || view === "top-artists")
        return this.navigate({ view: "search", q: item.name, page: 1 });
      return this.navigate({
        view: "detail",
        kind: view,
        id: item.id || view,
        name: labels[view] || item.name,
        back: view,
      });
    }
    async renderOverview(epoch) {
      const head = this.heading(
        "Your music, together",
        "Browse your library, find shared playlists, and see what is happening.",
      );
      head.appendChild(
        this.button("Refresh Spotify library", (e) => {
          this.indexing = true;
          return this.operation({ kind: "refresh" }, e.currentTarget);
        }),
      );
      const quick = el("div", "jb-quick");
      for (const view of ["mine", "liked", "shared", "downloads"])
        quick.appendChild(
          this.button(labels[view], () => this.navigate({ view })),
        );
      this.main.appendChild(quick);
      const grid = el("div", "jb-overview");
      this.main.appendChild(grid);
      const shelves = [
        [
          "Your playlists",
          "Browser/Collections?view=mine&pageSize=6&sort=imported&direction=desc",
          "mine",
        ],
        [
          "Shared on this server",
          "Browser/Collections?view=shared&pageSize=6&sort=shared&direction=desc",
          "shared",
        ],
      ];
      if (this.me?.linked)
        shelves.push(
          ["Recently played", "Browser/Spotify/recent?pageSize=6", "recent"],
          [
            "Your top tracks",
            "Browser/Spotify/top-tracks?pageSize=6",
            "top-tracks",
          ],
          ["Saved albums", "Browser/Spotify/albums?pageSize=6", "albums"],
        );
      for (const [title, path, view] of shelves) {
        const shelf = el("section", "jb-shelf"),
          header = el("div", "jb-section-head");
        header.append(
          el("h2", "", title),
          this.button(
            "View all",
            () => this.navigate({ view }),
            "jb-text-button",
          ),
        );
        shelf.appendChild(header);
        grid.appendChild(shelf);
        const paint = (data) => {
          shelf.querySelector(".jb-skeleton")?.remove();
          shelf.querySelector(".jb-cards")?.remove();
          shelf.querySelector(".jb-empty-copy")?.remove();
          if (!data.items?.length) {
            shelf.appendChild(
              el(
                "p",
                "jb-muted jb-empty-copy",
                data.complete
                  ? "Nothing here yet."
                  : "Building your library index. You can keep browsing.",
              ),
            );
            return;
          }
          const cards = el("div", "jb-cards");
          data.items.forEach((item) =>
            cards.appendChild(this.card(item, view)),
          );
          shelf.appendChild(cards);
        };
        const cached = this.cache.get(path);
        if (cached) paint(cached);
        else this.loading(shelf);
        this.api(path)
          .then((data) => {
            if (epoch === this.epoch) paint(data);
          })
          .catch(() => {
            if (epoch === this.epoch) {
              shelf.querySelector(".jb-skeleton")?.remove();
              shelf.appendChild(
                this.button(
                  "Could not load. Retry",
                  () => this.navigate(this.route, false),
                  "jb-text-button",
                ),
              );
            }
          });
      }
    }
    toolbar(defaults, sorts, changed, filters = []) {
      const preferences = this.pref(defaults),
        controls = el("div", "jb-toolbar"),
        search = el("input", "jb-filter");
      search.type = "search";
      search.placeholder = "Filter this view";
      search.setAttribute("aria-label", "Filter this view");
      search.value = preferences.q || "";
      let debounce;
      const update = (patch) => {
        const before = { ...preferences };
        Object.assign(preferences, patch);
        this.remember(preferences);
        changed(preferences, before);
      };
      search.oninput = () => {
        clearTimeout(debounce);
        debounce = setTimeout(() => update({ q: search.value, page: 1 }), 250);
      };
      controls.appendChild(search);
      const sort = el("select");
      sort.setAttribute("aria-label", "Sort by");
      for (const [value, title] of sorts) {
        const o = el("option", "", title);
        o.value = value;
        sort.appendChild(o);
      }
      sort.value = preferences.sort;
      sort.onchange = () => update({ sort: sort.value, page: 1 });
      controls.appendChild(sort);
      controls.appendChild(
        this.button(
          preferences.direction === "desc" ? "Descending ↓" : "Ascending ↑",
          () =>
            update({
              direction: preferences.direction === "desc" ? "asc" : "desc",
              page: 1,
            }),
        ),
      );
      if (filters.length) {
        const f = el("select");
        f.setAttribute("aria-label", "Filter status");
        for (const [value, title] of filters) {
          const o = el("option", "", title);
          o.value = value;
          f.appendChild(o);
        }
        f.value = preferences.filter || "";
        f.onchange = () => update({ filter: f.value, page: 1 });
        controls.appendChild(f);
      }
      this.main.appendChild(controls);
      return preferences;
    }
    params(p) {
      const values = {
        page: p.page || 1,
        pageSize: 50,
        sort: p.sort || "name",
        direction: p.direction || "asc",
      };
      if (p.q) values.q = p.q;
      if (p.filter) values.filter = p.filter;
      return new URLSearchParams(values).toString();
    }
    pager(host, data, p, changed) {
      const footer = el("div", "jb-pager"),
        pages = Math.max(1, Math.ceil(data.total / data.pageSize)),
        previous = this.button("Previous", () =>
          changed({ ...p, page: data.page - 1 }),
        ),
        next = this.button("Next", () =>
          changed({ ...p, page: data.page + 1 }),
        );
      previous.disabled = data.page <= 1;
      next.disabled = data.page >= pages;
      footer.append(
        previous,
        el(
          "span",
          "jb-muted",
          `${data.total} results · Page ${data.page} of ${pages}${data.complete === false ? " · Index building" : ""}`,
        ),
        next,
      );
      host.appendChild(footer);
    }
    async renderLibrary(epoch) {
      const view = this.route.view;
      this.heading(labels[view] || "Library");
      this.subnav([
        "mine",
        "playlists",
        "liked",
        "albums",
        "artists",
        "shared",
        "following",
      ]);
      if (["liked", "recent", "top-tracks"].includes(view))
        return this.renderDetail(epoch, true);
      const local = ["mine", "shared", "following"].includes(view),
        defaults = {
          sort:
            view === "shared"
              ? "shared"
              : view === "following"
                ? "followed"
                : local
                  ? "imported"
                  : "name",
          direction: local ? "desc" : "asc",
          page: 1,
          mode: "grid",
        },
        sorts = local
          ? [
              ["name", "Name"],
              ["imported", "Imported date"],
              ["updated", "Last synced"],
              ...(view === "shared"
                ? [
                    ["shared", "Shared date"],
                    ["owner", "Owner"],
                  ]
                : view === "following"
                  ? [["followed", "Followed date"]]
                  : []),
            ]
          : [
              ["name", "Name"],
              ...(view === "albums" ? [["added", "Added to Spotify"]] : []),
            ];
      const refresh = (p) => {
          this.remember(p);
          this.navigate(this.route, false);
        },
        p = this.toolbar(
          defaults,
          sorts,
          refresh,
          local
            ? [
                ["", "All playlists"],
                ["private", "Private"],
                ["shared", "Shared"],
              ]
            : [],
        );
      this.main
        .querySelector(".jb-toolbar")
        .appendChild(
          this.button(p.mode === "list" ? "Grid view" : "List view", () =>
            refresh({ ...p, mode: p.mode === "list" ? "grid" : "list" }),
          ),
        );
      const path = local
          ? "Browser/Collections?view=" + view + "&" + this.params(p)
          : "Browser/Spotify/" + view + "?" + this.params(p),
        host = el("div");
      this.main.appendChild(host);
      this.loading(host);
      const data = await this.api(path);
      if (epoch !== this.epoch) return;
      host.innerHTML = "";
      const cards = el(
        "div",
        p.mode === "list" ? "jb-cards is-list" : "jb-cards",
      );
      data.items.forEach((item) => cards.appendChild(this.card(item, view)));
      host.appendChild(cards);
      if (!data.items.length)
        host.appendChild(
          el(
            "div",
            "jb-empty",
            data.complete
              ? "No matches. Try a different filter or import a Spotify playlist."
              : "Building your Spotify index. Results appear as each section finishes.",
          ),
        );
      this.pager(host, data, p, refresh);
    }
    selection() {
      const key = this.key();
      if (!this.selections.has(key))
        this.selections.set(key, { entries: new Map(), all: false });
      return this.selections.get(key);
    }
    async renderDetail(epoch, inline = false) {
      const route = this.route,
        local = !!route.collectionId,
        kind = route.kind || route.view,
        id = route.id || kind;
      if (!inline)
        this.main.appendChild(
          this.button(
            "← " + (labels[route.back] || "Library"),
            () => this.navigate({ view: route.back || "mine" }),
            "jb-text-button",
          ),
        );
      let meta = {
        name: route.name || labels[kind] || "Tracks",
        owned: true,
        sourceId: id,
      };
      if (local)
        meta = await this.api(
          "Browser/Collections/" + encodeURIComponent(route.collectionId),
        );
      if (epoch !== this.epoch) return;
      const head = inline
          ? this.main.querySelector(".jb-view-head")
          : this.heading(
              meta.name,
              local
                ? `${meta.ownerName} · ${meta.shared ? "Shared" : "Private"} · Last synced ${date(meta.updatedAtUtc)}`
                : "Play available Jellyfin audio. Download missing tracks separately.",
            ),
        actions = el("div", "jb-actions");
      head.appendChild(actions);
      const endpoint = local
        ? "Browser/Collections/" +
          encodeURIComponent(route.collectionId) +
          "/Tracks"
        : "Browser/Spotify/" + kind + "/" + encodeURIComponent(id) + "/Tracks";
      const p = this.toolbar(
        {
          sort: kind === "liked" ? "added" : "original",
          direction: kind === "liked" ? "desc" : "asc",
          page: 1,
        },
        [
          [
            "original",
            kind === "album" ? "Disc / track order" : "Original order",
          ],
          ["title", "Title"],
          ["artist", "Artist"],
          ["album", "Album"],
          ["duration", "Duration"],
          ["added", "Added to Spotify"],
        ],
        (next, before) => {
          if (next.q !== before.q || next.filter !== before.filter)
            this.selection().all = false;
          this.remember(next);
          this.navigate(route, false);
        },
        [
          ["", "All tracks"],
          ["available", "Available in Jellyfin"],
          ["missing", "Unavailable"],
        ],
      );
      const selection = this.selection(),
        bar = el("div", "jb-selection"),
        count = el("span", "jb-muted"),
        host = el("div");
      this.main.append(bar, host);
      this.loading(host);
      const data = await this.api(endpoint + "?" + this.params(p));
      if (epoch !== this.epoch) return;
      if (!data.complete && !local) {
        host.innerHTML = "";
        host.appendChild(
          el(
            "p",
            "jb-muted",
            "Preparing this collection. You can keep browsing.",
          ),
        );
        if (kind === "playlist" || kind === "album") {
          this.indexing = true;
          await this.operation({ kind: "inspect-" + kind, sourceId: id });
        } else await this.ensureIndex();
        return;
      }
      const selected = () => ({
        kind: "tracks",
        ...(local
          ? { collectionId: route.collectionId }
          : { sourceId: id, sourceName: meta.name }),
        trackIds: [...new Set(selection.entries.values())],
        selectAll: selection.all,
        query: p.q,
        filter: p.filter,
        catalogKind: local
          ? undefined
          : kind === "playlist" || kind === "album"
            ? kind + ":" + id
            : kind,
      });
      const paint = () => {
        host.innerHTML = "";
        count.textContent = selection.all
          ? "All matching tracks selected"
          : selection.entries.size + " selected";
        const table = el("table", "jb-track-table"),
          thead = el("thead"),
          header = el("tr");
        for (const t of [
          "",
          "#",
          "Title / artist",
          "Album",
          "Time",
          "Added to Spotify",
          "Availability",
          "",
        ])
          header.appendChild(el("th", "", t));
        thead.appendChild(header);
        table.appendChild(thead);
        const tbody = el("tbody");
        table.appendChild(tbody);
        for (const entry of data.items) {
          const track = entry.track,
            row = el("tr");
          row.dataset.entryId = entry.entryId;
          const cell = el("td"),
            box = el("input");
          box.type = "checkbox";
          box.checked = selection.all || selection.entries.has(entry.entryId);
          box.setAttribute("aria-label", "Select " + track.name);
          box.onchange = () => {
            if (selection.all) {
              selection.all = false;
              selection.entries.clear();
              data.items
                .filter((e) => e.entryId !== entry.entryId)
                .forEach((e) => selection.entries.set(e.entryId, e.track.id));
            }
            if (box.checked) selection.entries.set(entry.entryId, track.id);
            else selection.entries.delete(entry.entryId);
            count.textContent = selection.entries.size + " selected";
          };
          cell.appendChild(box);
          row.append(cell, el("td", "jb-number", entry.position + 1));
          const title = el("td"),
            content = el("div", "jb-track-name"),
            copy = el("div");
          content.appendChild(this.art(track.coverUrl));
          copy.append(
            el("strong", "", track.name),
            el("span", "jb-muted", (track.artists || []).join(", ")),
          );
          const extra = el("details", "jb-track-extra");
          extra.append(
            el("summary", "", "Details"),
            el(
              "span",
              "jb-muted",
              `${track.album} · ${duration(track.durationMs)} · Added to Spotify ${exact(track.addedAtUtc)}`,
            ),
          );
          copy.appendChild(extra);
          content.appendChild(copy);
          title.appendChild(content);
          row.append(
            title,
            el("td", "jb-album", track.album),
            el("td", "jb-duration", duration(track.durationMs)),
          );
          const added = el("td", "jb-date", date(track.addedAtUtc));
          added.title = exact(track.addedAtUtc);
          row.append(
            added,
            el(
              "td",
              "jb-availability",
              entry.itemId ? "Available" : "Unavailable",
            ),
          );
          const control = el("td");
          if (entry.itemId)
            control.appendChild(
              this.button(
                "Play",
                (e) => this.play(endpoint, p, entry.entryId, e.currentTarget),
                "jb-text-button",
              ),
            );
          else if (!local || meta.owned)
            control.appendChild(
              this.button(
                "Download",
                (e) =>
                  this.operation(
                    { kind: "tracks", trackIds: [track.id] },
                    e.currentTarget,
                  ),
                "jb-text-button",
              ),
            );
          row.appendChild(control);
          tbody.appendChild(row);
        }
        host.appendChild(table);
        if (!data.items.length)
          host.appendChild(el("div", "jb-empty", "No matching tracks."));
        this.pager(host, data, p, (next) => {
          this.remember(next);
          this.navigate(route, false);
        });
      };
      bar.append(
        this.button("Select this page", () => {
          data.items.forEach((e) =>
            selection.entries.set(e.entryId, e.track.id),
          );
          paint();
        }),
        this.button("Select all matching", () => {
          selection.all = true;
          data.items.forEach((e) =>
            selection.entries.set(e.entryId, e.track.id),
          );
          paint();
        }),
        this.button("Clear selection", () => {
          selection.all = false;
          selection.entries.clear();
          paint();
        }),
        count,
      );
      if (!local || meta.owned)
        bar.appendChild(
          this.button(
            "Download selected",
            (e) => {
              if (!selection.all && !selection.entries.size) {
                this.message("Select tracks first.");
                return;
              }
              return this.operation(selected(), e.currentTarget);
            },
            "jb-button is-primary",
          ),
        );
      actions.appendChild(
        this.button(
          "Play available",
          (e) => this.play(endpoint, p, null, e.currentTarget),
          "jb-button is-primary",
        ),
      );
      if (!local || meta.owned) {
        actions.appendChild(
          this.button("Download missing", (e) =>
            this.operation(
              local
                ? {
                    kind: "tracks",
                    collectionId: route.collectionId,
                    selectAll: true,
                    filter: "missing",
                  }
                : {
                    kind: "tracks",
                    selectAll: true,
                    catalogKind:
                      kind === "playlist" || kind === "album"
                        ? kind + ":" + id
                        : kind,
                    filter: "missing",
                  },
              e.currentTarget,
            ),
          ),
        );
        const toggle = el("label", "jb-toggle"),
          input = el("input");
        input.type = "checkbox";
        input.checked = !!meta.monitored;
        input.onchange = async () => {
          input.disabled = true;
          try {
            await this.api(
              local
                ? `Browser/Collections/${route.collectionId}/Monitoring`
                : `Browser/Spotify/${kind}/${id}/Monitoring`,
              "POST",
              { enabled: input.checked },
            );
            this.message(
              input.checked
                ? "Automatic monitoring enabled. Future syncs request missing tracks."
                : "Automatic monitoring disabled.",
            );
          } finally {
            input.disabled = false;
          }
        };
        toggle.append(input, el("span", "", "Keep in sync"));
        if (["playlist", "album", "liked"].includes(kind) || local)
          actions.appendChild(toggle);
      }
      if (local) {
        if (meta.owned) {
          actions.appendChild(
            this.button(
              meta.shared ? "Make private" : "Share playlist",
              async () => {
                await this.api(
                  `Collections/${route.collectionId}/Sharing`,
                  "POST",
                  { shared: !meta.shared },
                );
                this.cache.clear();
                this.navigate(route, false);
              },
            ),
          );
          actions.appendChild(
            this.button(
              "Remove",
              async () => {
                if (
                  !(await this.confirm(
                    "Remove this playlist? Monitoring will stop. Downloaded audio will stay.",
                  ))
                )
                  return;
                await this.api(`Collections/${route.collectionId}`, "DELETE");
                this.navigate({ view: "mine" });
              },
              "jb-text-button is-danger",
            ),
          );
        } else
          actions.appendChild(
            this.button(meta.following ? "Unfollow" : "Follow", async () => {
              await this.api(
                `Collections/${route.collectionId}/Follow`,
                meta.following ? "DELETE" : "POST",
              );
              this.navigate(route, false);
            }),
          );
      }
      paint();
    }
    async play(endpoint, p, entryId, button) {
      button.disabled = true;
      try {
        const ids = [];
        let page = 1,
          total = Infinity;
        while (page === 1 || (page - 1) * 50 < total) {
          const data = await this.api(
            endpoint + "?" + this.params({ ...p, filter: "available", page }),
          );
          total = data.total;
          data.items
            .filter((e) => !entryId || e.entryId === entryId)
            .forEach((e) => {
              if (e.itemId) ids.push(e.itemId);
            });
          if (
            !data.items.length ||
            page * data.pageSize >= total ||
            (entryId && ids.length)
          )
            break;
          page++;
        }
        if (!ids.length) {
          this.message(
            "No playable audio is available in your Jellyfin libraries yet. Downloads may be waiting for a library scan.",
          );
          return;
        }
        this.nativePlay(ids);
      } finally {
        button.disabled = false;
      }
    }
    nativePlay(ids) {
      const host = el("div");
      host.hidden = true;
      host.innerHTML =
        '<div is="emby-itemscontainer" class="itemsContainer"></div>';
      const container = host.firstElementChild;
      container.fetchData = async () => {
        const items = new Map();
        for (let offset = 0; offset < ids.length; offset += 100) {
          if (!this.current()) throw new Error("Account changed.");
          const query = new URLSearchParams({
            Ids: ids.slice(offset, offset + 100).join(","),
            Limit: 100,
            Fields: "MediaSources",
            EnableTotalRecordCount: false,
          });
          const data = await plugin.apiRequest({
            type: "GET",
            url:
              ApiClient.getUrl(
                "Users/" + ApiClient.getCurrentUserId() + "/Items",
              ) +
              "?" +
              query,
            dataType: "json",
          });
          for (const item of data.Items || data.items || [])
            items.set(item.Id || item.id, item);
        }
        return { Items: ids.map((id) => items.get(id)).filter(Boolean) };
      };
      for (const id of ids.slice(0, 1)) {
        const b = el("button", "itemAction");
        b.type = "button";
        b.dataset.id = id;
        b.dataset.serverid = ApiClient.serverId();
        b.dataset.type = b.dataset.mediatype = "Audio";
        b.dataset.action = "playallfromhere";
        container.appendChild(b);
      }
      this.root.appendChild(host);
      setTimeout(() => {
        if (!this.current()) {
          host.remove();
          return;
        }
        const e = new MouseEvent("click", { bubbles: true, cancelable: true });
        container.firstElementChild.dispatchEvent(e);
        host.remove();
        if (!e.defaultPrevented)
          this.message(
            "Jellyfin player unavailable. Reload and try again.",
            true,
          );
      }, 0);
    }
    async renderDownloads(epoch) {
      const head = this.heading(
        "Downloads",
        "Your requests share audio jobs with other users. Cancelling never deletes shared audio.",
      );
      this.subnav(["downloads", "history"]);
      head.appendChild(
        this.button(
          this.activity?.paused ? "Resume my downloads" : "Pause my downloads",
          async () => {
            await this.api("Requests/Actions", "POST", {
              action: "pause",
              pauseAll: !this.activity?.paused,
            });
            await this.poll();
            this.navigate(this.route, false);
          },
        ),
      );
      const p = this.toolbar(
        { sort: "active", direction: "asc", page: 1 },
        [
          ["active", "Active first"],
          ["requested", "Requested date"],
          ["updated", "Last updated"],
          ["priority", "Priority"],
          ["title", "Title"],
          ["status", "Status"],
        ],
        (next) => {
          this.remember(next);
          this.navigate(this.route, false);
        },
        [
          ["", "All statuses"],
          ...[
            "Pending",
            "Matching",
            "Downloading",
            "Completed",
            "Failed",
            "Paused",
            "Cancelled",
          ].map((v) => [v, v]),
        ],
      );
      const selection = this.selection(),
        bulk = el("div", "jb-selection"),
        count = el("span", "jb-muted");
      let currentRequests = [];
      bulk.append(
        this.button("Select this page", () => {
          currentRequests.forEach((item) =>
            selection.entries.set(item.id, item.trackId),
          );
          this.main
            .querySelectorAll(".jb-download-table input[type=checkbox]")
            .forEach((box) => (box.checked = true));
          count.textContent = selection.entries.size + " selected";
        }),
        this.button("Clear selection", () => {
          selection.entries.clear();
          this.main
            .querySelectorAll(".jb-download-table input[type=checkbox]")
            .forEach((box) => (box.checked = false));
          count.textContent = "0 selected";
        }),
      );
      const action = async (
        name,
        ids = [...selection.entries.keys()],
        priority,
      ) => {
        if (!ids.length) {
          this.message("Select requests first.");
          return;
        }
        await this.api("Requests/Actions", "POST", {
          action: name,
          ids,
          priority,
        });
        selection.entries.clear();
        await this.refreshDownloads();
        await this.poll();
        if (name === "cancel")
          this.message(
            "Requests cancelled. Keep in sync may request these tracks again on a later sync.",
          );
      };
      for (const [name, label] of [
        ["retry", "Retry"],
        ["pause", "Pause"],
        ["resume", "Resume"],
        ["cancel", "Cancel"],
        ["clear", "Clear history"],
      ])
        bulk.appendChild(this.button(label, () => action(name)));
      const priority = el("select");
      priority.setAttribute("aria-label", "Priority for selected requests");
      for (const [value, label] of [
        [1, "High priority"],
        [0, "Normal priority"],
        [-1, "Low priority"],
      ]) {
        const o = el("option", "", label);
        o.value = value;
        priority.appendChild(o);
      }
      priority.value = "0";
      bulk.append(
        priority,
        this.button("Set priority", () =>
          action("priority", undefined, Number(priority.value)),
        ),
        count,
      );
      this.main.appendChild(bulk);
      const host = el("div"),
        table = el("table", "jb-track-table jb-download-table"),
        thead = el("thead"),
        header = el("tr");
      this.main.appendChild(host);
      [
        "",
        "Track",
        "Status",
        "Source",
        "Requested",
        "Downloaded",
        "Priority",
        "",
      ].forEach((t) => header.appendChild(el("th", "", t)));
      thead.appendChild(header);
      const tbody = el("tbody");
      table.append(thead, tbody);
      host.appendChild(table);
      let oldRevision;
      this.refreshDownloads = async () => {
        const data = await this.api("Requests?" + this.params(p));
        if (epoch !== this.epoch || data.revision === oldRevision) return;
        oldRevision = data.revision;
        currentRequests = data.items;
        const keep = new Set();
        for (const item of data.items) {
          keep.add(item.id);
          let row = [...tbody.children].find((n) => n.dataset.id === item.id);
          const signature = JSON.stringify(item);
          if (row?.dataset.signature === signature) continue;
          if (!row) {
            row = el("tr");
            row.dataset.id = item.id;
            row.append(...Array.from({ length: 8 }, () => el("td")));
            tbody.appendChild(row);
          }
          row.dataset.signature = signature;
          const cells = row.children;
          if (!cells[0].firstChild) {
            const box = el("input");
            box.type = "checkbox";
            box.setAttribute("aria-label", "Select request " + item.title);
            box.onchange = () => {
              if (box.checked) selection.entries.set(item.id, item.trackId);
              else selection.entries.delete(item.id);
              count.textContent = selection.entries.size + " selected";
            };
            cells[0].appendChild(box);
          }
          cells[0].firstChild.checked = selection.entries.has(item.id);
          cells[1].textContent = item.title + " — " + item.artists;
          cells[2].textContent =
            (item.status === "Downloading" && item.stage
              ? item.stage
              : item.status) +
            (item.jobStatus === "Completed" && item.status !== "Cancelled"
              ? " · scan may be pending"
              : "");
          if (item.progress != null && item.jobStatus === "Downloading") {
            const progress = el("progress");
            progress.max = 1;
            progress.value = item.progress;
            progress.setAttribute("aria-label", "Download progress");
            cells[2].appendChild(progress);
          }
          cells[2].title = item.error || "";
          const extra = el("details", "jb-track-extra");
          extra.append(
            el("summary", "", "Details"),
            el(
              "span",
              "jb-muted",
              `Source: ${item.sourceName || "Selection"} · Requested ${exact(item.requestedAtUtc)} · Downloaded ${exact(item.downloadedAtUtc)} · Priority ${["Low", "Normal", "High"][item.priority + 1]}`,
            ),
          );
          cells[1].appendChild(extra);
          cells[3].textContent = item.sourceName || "Selection";
          cells[4].textContent = date(item.requestedAtUtc);
          cells[4].title = exact(item.requestedAtUtc);
          cells[5].textContent = date(item.downloadedAtUtc);
          cells[6].textContent = ["Low", "Normal", "High"][item.priority + 1];
          if (cells[7].dataset.status !== item.status) {
            cells[7].innerHTML = "";
            const menu = el("details", "jb-request-actions");
            menu.appendChild(el("summary", "", "Actions"));
            const options = el("div");
            for (const [name, label] of [
              ["retry", "Retry request"],
              ["pause", "Pause request"],
              ["resume", "Resume request"],
              ["cancel", "Cancel request"],
            ])
              options.appendChild(
                this.button(
                  label,
                  () => action(name, [item.id]),
                  "jb-text-button",
                ),
              );
            for (const [value, label] of [
              [1, "High priority"],
              [0, "Normal priority"],
              [-1, "Low priority"],
            ])
              options.appendChild(
                this.button(
                  label,
                  () => action("priority", [item.id], value),
                  "jb-text-button",
                ),
              );
            menu.appendChild(options);
            cells[7].appendChild(menu);
            cells[7].dataset.status = item.status;
          }
        }
        [...tbody.children].forEach((row) => {
          if (!keep.has(row.dataset.id)) row.remove();
        });
        data.items.forEach((item, i) => {
          const row = [...tbody.children].find((n) => n.dataset.id === item.id);
          if (tbody.children[i] !== row)
            tbody.insertBefore(row, tbody.children[i] || null);
        });
        host.querySelector(".jb-pager")?.remove();
        this.pager(host, data, p, (next) => {
          this.remember(next);
          this.navigate(this.route, false);
        });
        count.textContent = selection.entries.size + " selected";
        if (!data.items.length && !host.querySelector(".jb-empty"))
          host.appendChild(el("p", "jb-empty", "No requests match this view."));
        else if (data.items.length) host.querySelector(".jb-empty")?.remove();
      };
      await this.refreshDownloads();
    }
    async renderHistory(epoch) {
      this.heading(
        "Sync & request history",
        "Sync discovers music and creates requests. Downloads continue independently.",
      );
      this.subnav(["downloads", "history"]);
      const preferences = this.toolbar(
        { sort: "updated", direction: "desc", page: 1 },
        [["updated", "Last updated"]],
        (next) => {
          this.remember(next);
          this.navigate(this.route, false);
        },
        [
          ["", "All operations"],
          ...[
            "Queued",
            "Running",
            "Completed",
            "PartiallyFailed",
            "Cancelled",
            "Failed",
          ].map((v) => [v, v]),
        ],
      );
      const host = el("div");
      this.main.appendChild(host);
      this.refreshHistory = async () => {
        const data = await this.api("Operations?" + this.params(preferences));
        if (epoch !== this.epoch) return;
        const signature = data.revision;
        if (host.dataset.revision === signature) return;
        host.dataset.revision = signature;
        const focus = document.activeElement?.dataset.operation;
        host.innerHTML = "";
        host.appendChild(
          el(
            "p",
            "jb-muted",
            "Last successful sync: " +
              exact(this.activity?.lastSuccessfulSyncUtc) +
              " · " +
              (this.activity?.schedule ||
                "Managed by Jellyfin scheduled tasks"),
          ),
        );
        for (const o of data.items || []) {
          const card = el("article", "jb-operation");
          card.append(
            el(
              "h3",
              "",
              `${o.kind || o.input?.kind || "Operation"} · ${o.status}`,
            ),
            el("p", "", o.stage),
            el(
              "p",
              "jb-muted",
              `${o.currentCollection || ""} · ${o.processed} processed · ${o.requested} requests · ${o.existing} already present · ${o.failed} failures`,
            ),
            el(
              "time",
              "jb-date",
              "Started " + exact(o.startedAtUtc || o.createdAtUtc),
            ),
          );
          if (o.totalKnown && o.discovered > 0) {
            const progress = el("progress");
            progress.max = o.discovered;
            progress.value = o.processed;
            card.appendChild(progress);
          }
          if (o.error) card.appendChild(el("p", "is-error", o.error));
        for (const issue of o.issues || []) card.appendChild(el("p", "is-error", issue.name + ": " + issue.message));
          if (["Queued", "Running"].includes(o.status)) {
            const cancel = this.button("Cancel preparation", async () => {
              await this.api("Operations/" + o.id + "/Cancel", "POST");
              this.message(
                "Cancellation requested. Existing downloads are kept.",
              );
              await this.poll();
            });
            cancel.dataset.operation = o.id;
            card.appendChild(cancel);
          }
          host.appendChild(card);
        }
        this.pager(host, data, preferences, (next) => {
          this.remember(next);
          this.navigate(this.route, false);
        });
        if (!data.items?.length)
          host.appendChild(
            el("div", "jb-empty", "No sync or request operations yet."),
          );
        if (focus)
          [...host.querySelectorAll("[data-operation]")]
            .find((b) => b.dataset.operation === focus)
            ?.focus({ preventScroll: true });
      };
      await this.refreshHistory();
    }
    async renderAccount(epoch) {
      const me = await this.api("Me");
      if (epoch !== this.epoch) return;
      this.me = me;
      this.heading(
        "Your Spotify account",
        "Your account and automatic-sync preferences belong only to your Jellyfin user.",
      );
      const settings = me.settings || {},
        panel = el("section", "jb-settings");
      panel.appendChild(
        el(
          "h2",
          "",
          me.linked
            ? "Connected as " + (me.spotifyDisplayName || me.spotifyUserId)
            : me.connectionState === "relink_required"
              ? "Spotify needs to be linked again"
              : "Link your Spotify account",
        ),
      );
      const status = el(
        "p",
        "jb-muted",
        me.serverReady === false
          ? "Ask an administrator to configure Spotify application credentials."
          : "Shared playlists and existing requests remain available without a Spotify link.",
      );
      panel.appendChild(status);
      panel.appendChild(
        this.button(
          me.linked ? "Relink Spotify" : "Link Spotify",
          () =>
            plugin.startSpotifyLink(status, (linked) => {
              this.cache.clear();
              this.me = norm(linked);
              this.indexing = true;
              this.operation({ kind: "refresh" });
              this.navigate({ view: "account" }, false);
            }),
          "jb-button is-primary",
        ),
      );
      if (me.linked)
        panel.appendChild(
          this.button("Unlink", async () => {
            if (
              !(await this.confirm(
                "Unlink Spotify? Saved collections and shared audio will remain.",
              ))
            )
              return;
            await this.api("OAuth/Unlink", "POST");
            this.cache.clear();
            this.me.linked = false;
            this.navigate({ view: "account" }, false);
          }),
        );
      const form = el("form", "jb-settings-form"),
        enabled = el("input");
      enabled.type = "checkbox";
      enabled.checked = settings.enabled !== false;
      const enabledLabel = el("label", "jb-toggle");
      enabledLabel.append(enabled, el("span", "", "Enable automatic sync"));
      form.appendChild(enabledLabel);
      const liked = el("input");
      liked.type = "checkbox";
      liked.checked = !!settings.syncLikedSongs;
      const likedLabel = el("label", "jb-toggle");
      likedLabel.append(liked, el("span", "", "Keep Liked Songs in sync"));
      form.appendChild(likedLabel);
      const fields = [];
      for (const [key, title] of [
        ["includeArtistFilters", "Only include these artists"],
        ["excludeArtistFilters", "Exclude these artists"],
      ]) {
        const label = el("label", "jb-field", title),
          input = el("input");
        input.value = (settings[key] || []).join(", ");
        input.placeholder = "Comma-separated names";
        label.appendChild(input);
        form.appendChild(label);
        fields.push(input);
      }
      const submit = el("button", "jb-button is-primary", "Save preferences");
      submit.type = "submit";
      form.appendChild(submit);
      form.onsubmit = async (e) => {
        e.preventDefault();
        submit.disabled = true;
        try {
          await this.api("Settings", "POST", {
            enabled: enabled.checked,
            syncLikedSongs: liked.checked,
            includeArtistFilters: fields[0].value
              .split(",")
              .map((x) => x.trim())
              .filter(Boolean),
            excludeArtistFilters: fields[1].value
              .split(",")
              .map((x) => x.trim())
              .filter(Boolean),
          });
          this.message(
            "Preferences saved. Individual playlists use Keep in sync.",
          );
        } catch (error) {
          this.message(error.message, true);
        } finally {
          submit.disabled = false;
        }
      };
      panel.appendChild(form);
      panel.appendChild(
        this.button("Sync now", (e) =>
          this.operation({ kind: "sync" }, e.currentTarget),
        ),
      );
      panel.appendChild(
        el(
          "p",
          "jb-muted",
          `Last successful sync: ${exact(settings.lastSuccessfulSyncUtc)}. Scheduling is managed by Jellyfin.`,
        ),
      );
      this.main.appendChild(panel);
    }
    async renderSearch(epoch) {
      this.heading("Spotify search", this.route.q);
      const host = el("div");
      this.main.appendChild(host);
      this.loading(host);
      const data = await this.api(
        "Search?" +
          new URLSearchParams({
            q: this.route.q,
            type: "track,album,playlist,artist",
            limit: 10,
            offset: ((this.route.page || 1) - 1) * 10,
          }),
      );
      if (epoch !== this.epoch) return;
      host.innerHTML = "";
      for (const [kind, title] of [
        ["tracks", "Tracks"],
        ["albums", "Albums"],
        ["playlists", "Playlists"],
        ["artists", "Artists"],
      ]) {
        const group = data[kind];
        if (!group?.items?.length) continue;
        host.appendChild(el("h2", "", title));
        const cards = el("div", "jb-cards");
        for (const item of group.items.filter(Boolean)) {
          const model = {
              ...item,
              imageUrl: item.images?.[0]?.url || item.album?.images?.[0]?.url,
              artists: item.artists || [],
              type: kind.slice(0, -1),
            },
            card = this.card(model, kind);
          if (kind === "tracks") {
            card.querySelector("button").onclick = () =>
              item.album?.id
                ? this.navigate({
                    view: "detail",
                    kind: "album",
                    id: item.album.id,
                    name: item.album.name,
                    back: "overview",
                  })
                : this.message("No album metadata available.");
            card.appendChild(
              this.button(
                "Download",
                (e) =>
                  this.operation(
                    { kind: "tracks", trackIds: [item.id] },
                    e.currentTarget,
                  ),
                "jb-text-button",
              ),
            );
          }
          cards.appendChild(card);
        }
        host.appendChild(cards);
      }
      if (!host.children.length)
        host.appendChild(
          el("p", "jb-empty", "No results. Try a different search."),
        );
      const nav = el("div", "jb-pager");
      if ((this.route.page || 1) > 1)
        nav.appendChild(
          this.button("Previous", () =>
            this.navigate({ ...this.route, page: this.route.page - 1 }),
          ),
        );
      if (Object.values(data).some((g) => g?.next))
        nav.appendChild(
          this.button("Next", () =>
            this.navigate({ ...this.route, page: (this.route.page || 1) + 1 }),
          ),
        );
      host.appendChild(nav);
    }
    confirm(text) {
      return new Promise((resolve) => {
        const dialog = el("dialog", "jb-dialog");
        dialog.setAttribute("aria-label", "Confirm action");
        dialog.appendChild(el("p", "", text));
        const finish = (answer) => {
            dialog.close?.();
            dialog.remove();
            resolve(answer);
          },
          cancel = this.button("Cancel", () => finish(false)),
          confirm = this.button(
            "Confirm",
            () => finish(true),
            "jb-button is-primary",
          );
        dialog.append(cancel, confirm);
        dialog.oncancel = (e) => {
          e.preventDefault();
          finish(false);
        };
        this.root.appendChild(dialog);
        if (dialog.showModal) dialog.showModal();
        else dialog.setAttribute("open", "");
        cancel.focus();
      });
    }
  }
  window.JellySpotBrowser = Browser;
  plugin.renderBrowse = function (root) {
    if (root._jellyspotBrowser?.current()) return;
    root._jellyspotBrowser?.dispose();
    root._jellyspotBrowser = new Browser(root);
    this._browseRoot = root;
    if (this._pendingInner) {
      const view =
        {
          sync: "account",
          queue: "downloads",
          home: "overview",
          collections: "mine",
        }[this._pendingInner] || this._pendingInner;
      this._pendingInner = null;
      root._jellyspotBrowser.navigate({ view }, false);
    }
  };
  plugin.showLibrarySection = function (root, section) {
    root._jellyspotBrowser?.navigate({
      view:
        {
          home: "overview",
          sync: "account",
          queue: "downloads",
          collections: "mine",
        }[section] || section,
    });
  };
  const originalIdentityCheck = plugin.checkAccountIdentity.bind(plugin);
  plugin.checkAccountIdentity = function () {
    if (
      this._browseRoot?._jellyspotBrowser &&
      !this._browseRoot._jellyspotBrowser.current()
    )
      this._browseRoot._jellyspotBrowser.dispose();
    return originalIdentityCheck();
  };
})();
