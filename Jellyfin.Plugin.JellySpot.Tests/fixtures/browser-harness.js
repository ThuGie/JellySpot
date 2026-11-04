/* Test-only Jellyfin client adapter; all requests go to the local fixture server. */
window.fixtureIdentity = "user";
window.ApiClient = {
  serverId: () => "fixture",
  getCurrentUserId: () => window.fixtureIdentity,
  getUrl: (path) => "/" + path,
};
window.jellySpotPlugin = {
  collectionIdentity: () => "fixture:" + window.fixtureIdentity,
  apiRequest: async (options) => {
    const response = await fetch(options.url, {
      method: options.type || "GET",
      headers: {
        "Content-Type": "application/json",
        ...(options.headers || {}),
      },
      body: options.data,
    });
    if (!response.ok && response.status !== 304)
      throw Error("Request unavailable");
    if (options.type === "GET" && !options.dataType) return response;
    if (response.status === 204 || response.status === 304) return;
    return response.json();
  },
  checkAccountIdentity() {
    if (this._browseRoot && !this._browseRoot._jellyspotBrowser.current()) {
      this._browseRoot._jellyspotBrowser.dispose();
      this._browseRoot.innerHTML = "";
    }
  },
  startSpotifyLink(status, done) {
    status.textContent = "Fixture linking";
    done({ Linked: true });
  },
};
document.addEventListener("click", async (event) => {
  const action = event.target.closest(
    '.itemAction[data-action="playallfromhere"]',
  );
  if (action) {
    event.preventDefault();
    window.playedIds = action.parentElement.fetchData
      ? (await action.parentElement.fetchData()).Items.map((item) => item.Id)
      : [...action.parentElement.querySelectorAll("[data-id]")].map(
          (node) => node.dataset.id,
        );
  }
});
