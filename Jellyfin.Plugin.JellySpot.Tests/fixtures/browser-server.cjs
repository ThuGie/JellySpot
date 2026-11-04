const http = require("node:http"),
  fs = require("node:fs"),
  path = require("node:path");
const root = path.resolve(__dirname, "../..");
const stamp = "2026-11-04T09:30:00Z";
function createFixture() {
  const state = {
    shared: true,
    following: false,
    paused: false,
    linked: true,
    operations: [],
    requestRevision: 1,
    calls: [],
  };
  const cover = (i) =>
    "data:image/svg+xml," +
    encodeURIComponent(
      `<svg xmlns="http://www.w3.org/2000/svg" width="400" height="400"><defs><linearGradient id="g" x2="1" y2="1"><stop stop-color="${["#123b48", "#413752", "#354a3c", "#68523f", "#1d294a", "#633e4a"][i % 6]}"/><stop offset="1" stop-color="#141925"/></linearGradient></defs><rect width="400" height="400" fill="url(#g)"/><circle cx="300" cy="110" r="155" stroke="#ffffff22" fill="none" stroke-width="1"/><circle cx="300" cy="110" r="105" stroke="#ffffff22" fill="none"/><path d="M0 300 L400 200 L400 400 L0 400Z" fill="#ffffff08"/><text x="30" y="310" fill="#fff" font-family="sans-serif" font-size="24" letter-spacing="5">${["NIGHT DRIVE", "SUNDAY", "DISCOVERIES", "JAZZ NOTES", "AFTER HOURS", "INDIE"][i % 6]}</text><text x="32" y="340" fill="#ffffff99" font-family="sans-serif" font-size="12" letter-spacing="3">JELLYSPOT COLLECTION</text></svg>`,
    );
  const tracks = Array.from({ length: 125 }, (_, i) => ({
    EntryId: "entry" + i,
    Position: i,
    Track: {
      Id: "track" + i,
      Name: `Track ${String(i).padStart(3, "0")}`,
      Artists: [i % 2 ? "Night Drive" : "River & Stone"],
      Album: "Long way home",
      CoverUrl: cover(i),
      DurationMs: 180000,
      AddedAtUtc:
        i % 8
          ? `2026-10-${String((i % 28) + 1).padStart(2, "0")}T12:00:00Z`
          : null,
    },
    ItemId: i % 3 ? "audio" + i : null,
    Availability: i % 3 ? "Available" : "Unavailable",
  }));
  const collections = Array.from({ length: 12 }, (_, i) => ({
    Id: "collection" + i,
    Name:
      [
        "Night drive",
        "Sunday morning",
        "New discoveries",
        "Late night jazz",
        "After hours",
        "Indie essentials",
      ][i % 6] + (i >= 6 ? " II" : ""),
    SourceId: "source" + i,
    CoverUrl: cover(i),
    Owned: i % 3 !== 2,
    OwnerName: i % 3 === 2 ? "Morgan" : "Alex",
    Shared: i % 3 === 2,
    TrackCount: 125,
    ImportedAtUtc: stamp,
    SharedAtUtc: stamp,
    UpdatedAtUtc: stamp,
    Monitored: i % 2 === 0,
    Following: state.following,
  }));
  const requests = Array.from({ length: 65 }, (_, i) => ({
    Id: "request" + i,
    TrackId: "track" + i,
    Title: tracks[i].Track.Name,
    Artists: tracks[i].Track.Artists.join(", "),
    SourceName: "Night drive",
    Status:
      i < 3
        ? "Downloading"
        : i < 10
          ? "Pending"
          : i < 15
            ? "Failed"
            : "Completed",
    JobStatus:
      i < 3
        ? "Downloading"
        : i < 10
          ? "Pending"
          : i < 15
            ? "Failed"
            : "Completed",
    Priority: 0,
    RequestedAtUtc: i % 6 ? stamp : null,
    DownloadedAtUtc: i >= 15 ? stamp : null,
    UpdatedAtUtc: stamp,
    Progress: i < 3 ? 0.42 : null,
  }));
  function page(items, q, revision = "fixture") {
    const query = (q.get("q") || "").toLowerCase(),
      filter = q.get("filter") || "",
      sort = q.get("sort") || "name",
      desc = q.get("direction") === "desc";
    items = items.filter(
      (item) => !query || JSON.stringify(item).toLowerCase().includes(query),
    );
    if (filter)
      items = items.filter((item) =>
        filter === "available"
          ? !!item.ItemId
          : filter === "missing"
            ? !item.ItemId
            : filter === "shared"
              ? item.Shared
              : filter === "private"
                ? !item.Shared
                : item.Status === filter,
      );
    const field = {
      name: "Name",
      title: "Title",
      added: "AddedAtUtc",
      requested: "RequestedAtUtc",
      updated: "UpdatedAtUtc",
      priority: "Priority",
      status: "Status",
      artist: "Artists",
      album: "Album",
      duration: "DurationMs",
      imported: "ImportedAtUtc",
      shared: "SharedAtUtc",
      followed: "FollowedAtUtc",
    }[sort];
    if (field)
      items.sort((a, b) => {
        const x = (a.Track || a)[field],
          y = (b.Track || b)[field];
        if (x == null || y == null) return x == null ? (y == null ? 0 : 1) : -1;
        return String(x).localeCompare(String(y)) * (desc ? -1 : 1);
      });
    const number = Number(q.get("page") || 1),
      size = Math.min(100, Number(q.get("pageSize") || 50)),
      total = items.length;
    return {
      Items: items.slice((number - 1) * size, number * size),
      Total: total,
      Page: number,
      PageSize: size,
      Revision:
        revision +
        ":" +
        number +
        ":" +
        sort +
        ":" +
        query +
        ":" +
        filter +
        ":" +
        desc,
      Complete: true,
    };
  }
  const server = http.createServer(async (req, res) => {
    const url = new URL(req.url, "http://localhost");
    let body = "";
    for await (const chunk of req) body += chunk;
    const input = body ? JSON.parse(body) : {};
    if (url.pathname === "/browser.js") {
      res.setHeader("Content-Type", "application/javascript; charset=utf-8");
      res.end(
        fs.readFileSync(
          path.join(
            root,
            "Jellyfin.Plugin.JellySpot/Inject/jellyspot-browser.js",
          ),
        ),
      );
      return;
    }
    if (url.pathname === "/harness.js") {
      res.setHeader("Content-Type", "application/javascript; charset=utf-8");
      res.end(fs.readFileSync(path.join(__dirname, "browser-harness.js")));
      return;
    }
    if (url.pathname === "/browser.css") {
      res.setHeader("Content-Type", "text/css; charset=utf-8");
      res.end(
        fs.readFileSync(
          path.join(
            root,
            "Jellyfin.Plugin.JellySpot/Inject/jellyspot-browser.css",
          ),
        ),
      );
      return;
    }
    if (url.pathname.startsWith("/Users/") && url.pathname.endsWith("/Items")) {
      res.setHeader("Content-Type", "application/json");
      res.end(
        JSON.stringify({
          Items: (url.searchParams.get("Ids") || "")
            .split(",")
            .filter(Boolean)
            .map((Id) => ({
              Id,
              Name: "Synthetic",
              Type: "Audio",
              MediaType: "Audio",
              ServerId: "fixture",
            })),
        }),
      );
      return;
    }
    if (!url.pathname.startsWith("/JellySpot/")) {
      res.setHeader("Content-Type", "text/html; charset=utf-8");
      res.end(
        `<!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>JellySpot browser fixture</title><link rel="stylesheet" href="/browser.css"><style>body{margin:0;padding:24px;background:#101217;color:#edf0f4;font-family:Segoe UI,Arial,sans-serif;--backgroundColor:#101217;--theme-primary-color:#00a4dc;--secondaryTextColor:#a4aab4}.material-icons{font-size:0!important;}.material-icons:before{content:'●';font-size:16px}body.light{background:#fafafa;color:#20232a;--backgroundColor:#fafafa;--secondaryTextColor:#626773}button,input,select{font:inherit}</style></head><body><div id="root"></div><script src="/harness.js"></script><script src="/browser.js"></script><script>jellySpotPlugin.renderBrowse(document.querySelector('#root'));</script></body></html>`,
      );
      return;
    }
    state.calls.push({ path: url.pathname, method: req.method, input });
    let result;
    if (url.pathname.endsWith("/Me"))
      result = {
        Linked: state.linked,
        ConnectionState: state.linked ? "connected" : "unlinked",
        SpotifyDisplayName: "Alex",
        ServerReady: true,
        Settings: {
          Enabled: true,
          SyncLikedSongs: true,
          LastSuccessfulSyncUtc: stamp,
        },
      };
    else if (url.pathname.endsWith("/Activity"))
      result = {
        Counts: { Pending: 7, Downloading: 3, Failed: 5 },
        Paused: state.paused,
        Operations: state.operations,
        Schedule: "Managed by Jellyfin scheduled tasks",
        LastSuccessfulSyncUtc: stamp,
        Revision: "activity" + state.requestRevision + state.operations.length,
      };
    else if (url.pathname.endsWith("/Operations") && req.method === "POST") {
      const operation = {
        Id: "operation" + state.operations.length,
        Kind: input.kind,
        Status: "Queued",
        Stage: "Preparing request",
        Processed: 0,
        Discovered: 0,
        Requested: 0,
        Existing: 0,
        Failed: 0,
        CreatedAtUtc: stamp,
      };
      state.operations.unshift(operation);
      result = { Id: operation.Id, Status: "Queued" };
    } else if (url.pathname.endsWith("/Operations"))
      result = page(
        state.operations,
        url.searchParams,
        "ops" + state.operations.length,
      );
    else if (url.pathname.endsWith("/Requests/Actions")) {
      if (input.pauseAll != null) state.paused = input.pauseAll;
      for (const r of requests)
        if (input.ids?.includes(r.Id)) {
          if (input.action === "pause") r.Status = "Paused";
          if (input.action === "resume" || input.action === "retry")
            r.Status = "Pending";
          if (input.action === "cancel") r.Status = "Cancelled";
          if (input.action === "priority") r.Priority = input.priority;
        }
      state.requestRevision++;
      result = { Changed: input.ids?.length || 0 };
    } else if (url.pathname.endsWith("/Requests"))
      result = page(
        requests,
        url.searchParams,
        "requests" + state.requestRevision,
      );
    else if (url.pathname.endsWith("/Browser/Collections")) {
      let items = collections;
      const view = url.searchParams.get("view");
      if (view === "mine") items = items.filter((x) => x.Owned);
      else items = state.shared ? items.filter((x) => x.Shared) : [];
      result = page(items, url.searchParams);
    } else if (/\/Browser\/Collections\/[^/]+\/Tracks$/.test(url.pathname)) {
      const id = url.pathname.split("/").at(-2),
        c = collections.find((x) => x.Id === id);
      if (!c || (!c.Owned && !state.shared)) {
        res.statusCode = 404;
        res.end("{}");
        return;
      }
      result = page(tracks, url.searchParams);
    } else if (/\/Browser\/Collections\/[^/]+$/.test(url.pathname)) {
      const c = collections.find(
        (x) => x.Id === url.pathname.split("/").at(-1),
      );
      if (!c || (!c.Owned && !state.shared)) {
        res.statusCode = 404;
        res.end("{}");
        return;
      }
      result = { ...c, Following: state.following };
    } else if (url.pathname.includes("/Sharing")) {
      state.shared = input.shared;
      for (const c of collections)
        if (c.Id === url.pathname.split("/").at(-2)) c.Shared = input.shared;
      res.statusCode = 204;
      res.end();
      return;
    } else if (url.pathname.includes("/Follow")) {
      state.following = req.method === "POST";
      res.statusCode = 204;
      res.end();
      return;
    } else if (
      url.pathname.includes("/Monitoring") ||
      url.pathname.endsWith("/Settings")
    ) {
      res.statusCode = 204;
      res.end();
      return;
    } else if (
      url.pathname.includes("/Browser/Spotify/") &&
      url.pathname.endsWith("/Tracks")
    )
      result = page(tracks, url.searchParams);
    else if (url.pathname.includes("/Browser/Spotify/"))
      result = page(
        collections.map((c) => ({
          Id: c.Id,
          Name: c.Name,
          ImageUrl: c.CoverUrl,
          Artists: ["River & Stone"],
        })),
        url.searchParams,
      );
    else if (url.pathname.endsWith("/Search"))
      result = { tracks: { items: [], next: null } };
    else result = {};
    if (req.method === "GET" && result.Revision && req.headers["if-none-match"] === '"' + result.Revision + '"') { res.statusCode = 304; res.end(); return; }
    res.setHeader("Content-Type", "application/json");
    res.end(JSON.stringify(result));
  });
  return { server, state, tracks, collections, requests };
}
module.exports = { createFixture };
