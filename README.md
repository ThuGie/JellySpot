# JellySpot

Single Jellyfin plugin that links Spotify accounts, browses/searchs Spotify, and syncs selected playlists / liked songs into a local music folder. Audio is matched from **YouTube Music** using a spotDL-style scoring engine (ISRC → scored title/artist/duration match), then downloaded with YoutubeExplode + FFmpeg and tagged with Spotify metadata and cover art.

## Requirements

- Jellyfin **10.11.x** (.NET 9)
- **FFmpeg** from Jellyfin’s own encoder (Dashboard → Playback → Transcoding), then PATH. Optional override in admin settings.
- A [Spotify Developer](https://developer.spotify.com/dashboard) application
- Spotify **Premium for the Spotify app owner** is required under current Spotify Development Mode rules (end-user Free accounts can still authorize for library/metadata)
- **File Transformation** plugin — required for the home-screen **Spotify Browse** tab (same inject system as JellySeerr)

## Where to find JellySpot

After install + **restart Jellyfin**, open the **Home** screen. Next to Home / Favorites (and JellySeerr’s Movies / TV / Requests if you have it) you should see one tab:

| Tab | Purpose |
|---|---|
| **Spotify Browse** | Music only: library, liked songs, sync, and the download queue |

Inside that tab, use **Library · Liked · Sync · Queue**. Playlists, albums, and artists live under Library.

**Admin:** left Dashboard menu → **JellySpot** (settings), same pattern as JellySeerr / StreamReady.

**Everyone:** Home header → **Spotify Browse**. First time, link your own Spotify; the library then shows that account only.

If home tabs are missing: install **File Transformation**, confirm JellySpot is **Active**, then restart and hard-refresh the web UI.

## Icon

Catalog image follows the official Jellyfin UX plugin size **1920×1080** (`assets/jellyspot.png`).
Installed packages also include a **512×512** `thumb.png` referenced by `meta.json` `imagePath`.

## Install (plugin repository)

1. In Jellyfin: **Dashboard → Plugins → Repositories → +**
2. Add repository:

| Field | Value |
|---|---|
| Repository name | JellySpot |
| Repository URL | `https://raw.githubusercontent.com/ThuGie/JellySpot/main/manifest.json` |

3. Open **Catalog**, find **JellySpot**, install, then restart Jellyfin.

Releases are built by GitHub Actions. Tag a version to publish:

```bash
git tag v1.0.0.0
git push origin v1.0.0.0
```

That builds the plugin zip, creates a GitHub Release, and updates `manifest.json` on `main`.

## Manual / local install

```powershell
dotnet build Jellyfin.Plugin.JellySpot/Jellyfin.Plugin.JellySpot.csproj -c Release
./scripts/package.ps1 -JellyfinPluginsDir "$env:LOCALAPPDATA/jellyfin/plugins"
```

Or on Linux/macOS:

```bash
bash scripts/package.sh artifacts
# unzip artifacts/JellySpot_*.zip into <jellyfin-data>/plugins/JellySpot/
```

## Spotify app setup

1. Create an app in the Spotify Developer Dashboard.
2. Add redirect URI (must match plugin admin setting), for example:

```text
http://127.0.0.1:8096/JellySpot/OAuth/Callback
```

Use your real Jellyfin base URL/port if different.

3. Copy **Client ID** and **Client Secret** into **Dashboard → Plugins → JellySpot → Admin**.
4. Pick a **Music library** (auto-detected) or set a custom storage folder. FFmpeg is taken from Jellyfin unless you override it.
5. Each Jellyfin user opens **Spotify Browse** → **Sync** → **Link Spotify**, then picks Liked Songs / playlists.

## Features

| Area | What you get |
|---|---|
| Admin | Storage path, Spotify credentials, rate limits, format, match threshold |
| Spotify Browse | One Home tab: Library, liked songs, sync, and the download queue |
| Library skip | Songs already in a Jellyfin music library (ISRC or title/artist/duration) show as In library and are not downloaded again |
| My Sync | Per-user OAuth, monitored playlists, artist include/exclude filters, Sync now |
| Queue | Status, match scores, rematch failed/completed items |
| Sync task | Scheduled `JellySpot Sync` task; uses playlist `snapshot_id` to skip unchanged lists |

## Anti-ban / rate-limit behavior

- Global Spotify request pacing (configurable requests/sec)
- Honors HTTP `429` + `Retry-After` with jitter
- Disk cache for Spotify API responses (default 10 days)
- Playlist sync skips work when `snapshot_id` is unchanged
- Debounced UI search (max 10 results/page per Spotify Dev Mode)

### Spotify Dev Mode caveats

- New Development Mode apps are limited to **5 authorized users**
- Batch track endpoints and some browse APIs were removed for Dev Mode in 2026
- Extended Quota is hard to get for personal projects — plan for household-scale use

## Folder layout

```text
{StorageRoot}/
  {AlbumArtist}/
    {Album} ({Year})/
      {Track} - {Title}.m4a
      cover.jpg
  Playlists/
    {PlaylistName}.m3u8
```

## How matching works

Before YouTube Music is searched, JellySpot checks whether you already have the song:

1. A previous JellySpot download for that Spotify track, or
2. An audio file already in a Jellyfin **music** library (ISRC, or a tight title + artist + duration match). Edition tags like `(Explicit Version)` and a leading artist name on the album folder are ignored, so `Greatest Hits (1998)` and `2Pac Greatest Hits (Explicit Version) (1998)` count as the same release. Existing album folders with more tracks are reused for any truly missing files.

If it is not already owned:

1. Prefer cached `spotifyTrackId → youtubeVideoId`
2. Search YouTube Music by **ISRC** when available
3. Fall back to `"Artist - Title"` (songs, then videos)
4. Score candidates (artist, title fuzz, duration, album, verified bonus, forbidden-word penalties)
5. Accept when score ≥ configured minimum (default **80**)

## Legal note

JellySpot does **not** download audio from Spotify streams. Spotify is used as a catalog/metadata source; audio is obtained from YouTube/YouTube Music. You are responsible for complying with applicable laws and service terms.

## Development

```powershell
dotnet build Jellyfin.Plugin.JellySpot/Jellyfin.Plugin.JellySpot.csproj -c Release
```

Target ABI: Jellyfin 10.11 / `net9.0`.
