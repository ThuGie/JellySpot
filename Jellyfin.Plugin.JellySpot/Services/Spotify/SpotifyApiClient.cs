using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Jellyfin.Plugin.JellySpot.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellySpot.Services.Spotify;

public class SpotifyApiClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SpotifyAuthService _auth;
    private readonly SpotifyRateLimiter _rateLimiter;
    private readonly SpotifyCache _cache;
    private readonly ILogger<SpotifyApiClient> _logger;

    public SpotifyApiClient(
        IHttpClientFactory httpClientFactory,
        SpotifyAuthService auth,
        SpotifyRateLimiter rateLimiter,
        SpotifyCache cache,
        ILogger<SpotifyApiClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _auth = auth;
        _rateLimiter = rateLimiter;
        _cache = cache;
        _logger = logger;
    }

    public async Task<string?> SearchAsync(Guid userId, string query, string types, int limit, int offset, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 10);
        var path = $"/search?q={Uri.EscapeDataString(query)}&type={Uri.EscapeDataString(types)}&limit={limit}&offset={offset}";
        return await GetStringAsync(userId, path, $"search:{types}:{query}:{limit}:{offset}", TimeSpan.FromHours(1), ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SpotifyPlaylistInfo>> GetUserPlaylistsAsync(Guid userId, CancellationToken ct = default)
    {
        var playlists = new List<SpotifyPlaylistInfo>();
        var offset = 0;
        while (true)
        {
            var path = $"/me/playlists?limit=50&offset={offset}";
            var json = await GetStringAsync(userId, path, $"me:playlists:{offset}", TimeSpan.FromHours(6), ct).ConfigureAwait(false);
            if (json == null)
            {
                break;
            }

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("items", out var items))
            {
                break;
            }

            foreach (var item in items.EnumerateArray())
            {
                playlists.Add(ParsePlaylist(item));
            }

            if (!doc.RootElement.TryGetProperty("next", out var next) || next.ValueKind == JsonValueKind.Null)
            {
                break;
            }

            offset += 50;
        }

        return playlists;
    }

    public async Task<(SpotifyPlaylistInfo Playlist, IReadOnlyList<SpotifyTrackInfo> Tracks)?> GetPlaylistWithTracksAsync(
        Guid userId,
        string playlistId,
        CancellationToken ct = default)
    {
        var metaJson = await GetStringAsync(userId, $"/playlists/{playlistId}", $"playlist:{playlistId}", TimeSpan.FromDays(1), ct)
            .ConfigureAwait(false);
        if (metaJson == null)
        {
            return null;
        }

        using var metaDoc = JsonDocument.Parse(metaJson);
        var playlist = ParsePlaylist(metaDoc.RootElement);
        var tracks = new List<SpotifyTrackInfo>();

        var offset = 0;
        while (true)
        {
            var path = $"/playlists/{playlistId}/tracks?limit=50&offset={offset}";
            var json = await GetStringAsync(userId, path, $"playlist-tracks:{playlistId}:{offset}", TimeSpan.FromHours(12), ct)
                .ConfigureAwait(false);

            if (json == null)
            {
                break;
            }

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("items", out var items))
            {
                break;
            }

            var count = 0;
            foreach (var item in items.EnumerateArray())
            {
                count++;
                var trackEl = item.TryGetProperty("track", out var t) ? t
                    : item.TryGetProperty("item", out var i) ? i
                    : default;
                if (trackEl.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (trackEl.TryGetProperty("type", out var type) && type.GetString() != "track")
                {
                    continue;
                }

                var track = ParseTrack(trackEl);
                if (!string.IsNullOrEmpty(track.Id))
                {
                    tracks.Add(track);
                }
            }

            if (!doc.RootElement.TryGetProperty("next", out var next) || next.ValueKind == JsonValueKind.Null || count == 0)
            {
                break;
            }

            offset += 50;
        }

        return (playlist, tracks);
    }

    public async Task<IReadOnlyList<SpotifyTrackInfo>> GetLikedSongsAsync(Guid userId, CancellationToken ct = default)
    {
        var tracks = new List<SpotifyTrackInfo>();
        var offset = 0;
        while (true)
        {
            var path = $"/me/tracks?limit=50&offset={offset}";
            var json = await GetStringAsync(userId, path, $"me:tracks:{offset}", TimeSpan.FromHours(12), ct).ConfigureAwait(false);
            if (json == null)
            {
                break;
            }

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("items", out var items))
            {
                break;
            }

            var count = 0;
            foreach (var item in items.EnumerateArray())
            {
                count++;
                if (!item.TryGetProperty("track", out var trackEl))
                {
                    continue;
                }

                var track = ParseTrack(trackEl);
                if (!string.IsNullOrEmpty(track.Id))
                {
                    tracks.Add(track);
                }
            }

            if (!doc.RootElement.TryGetProperty("next", out var next) || next.ValueKind == JsonValueKind.Null || count == 0)
            {
                break;
            }

            offset += 50;
        }

        return tracks;
    }

    public async Task<SpotifyTrackInfo?> GetTrackAsync(Guid userId, string trackId, CancellationToken ct = default)
    {
        var json = await GetStringAsync(userId, $"/tracks/{trackId}", $"track:{trackId}", TimeSpan.FromDays(10), ct)
            .ConfigureAwait(false);
        if (json == null)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(json);
        return ParseTrack(doc.RootElement);
    }

    public async Task<IReadOnlyList<SpotifyTrackInfo>> GetAlbumTracksAsync(Guid userId, string albumId, CancellationToken ct = default)
    {
        var albumJson = await GetStringAsync(userId, $"/albums/{albumId}", $"album:{albumId}", TimeSpan.FromDays(10), ct)
            .ConfigureAwait(false);
        string? albumName = null;
        string? albumArtist = null;
        string? cover = null;
        int? year = null;
        if (albumJson != null)
        {
            using var albumDoc = JsonDocument.Parse(albumJson);
            albumName = albumDoc.RootElement.GetProperty("name").GetString();
            if (albumDoc.RootElement.TryGetProperty("artists", out var artists) && artists.GetArrayLength() > 0)
            {
                albumArtist = artists[0].GetProperty("name").GetString();
            }

            cover = GetBestImage(albumDoc.RootElement);
            if (albumDoc.RootElement.TryGetProperty("release_date", out var rd))
            {
                var s = rd.GetString();
                if (!string.IsNullOrEmpty(s) && s.Length >= 4 && int.TryParse(s[..4], out var y))
                {
                    year = y;
                }
            }
        }

        var tracks = new List<SpotifyTrackInfo>();
        var offset = 0;
        while (true)
        {
            var path = $"/albums/{albumId}/tracks?limit=50&offset={offset}";
            var json = await GetStringAsync(userId, path, $"album-tracks:{albumId}:{offset}", TimeSpan.FromDays(10), ct)
                .ConfigureAwait(false);
            if (json == null)
            {
                break;
            }

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("items", out var items))
            {
                break;
            }

            var count = 0;
            foreach (var item in items.EnumerateArray())
            {
                count++;
                var track = ParseTrack(item);
                track.Album = albumName ?? track.Album;
                track.AlbumArtist = albumArtist ?? track.AlbumArtist;
                track.CoverUrl = cover ?? track.CoverUrl;
                track.Year = year ?? track.Year;
                track.AlbumId = albumId;
                tracks.Add(track);
            }

            if (!doc.RootElement.TryGetProperty("next", out var next) || next.ValueKind == JsonValueKind.Null || count == 0)
            {
                break;
            }

            offset += 50;
        }

        return tracks;
    }

    private async Task<string?> GetStringAsync(
        Guid userId,
        string relativePath,
        string cacheKey,
        TimeSpan cacheTtl,
        CancellationToken ct)
    {
        var cached = await _cache.GetAsync(cacheKey, ct).ConfigureAwait(false);
        if (cached != null)
        {
            return cached;
        }

        var tokens = await _auth.GetValidTokensAsync(userId, ct).ConfigureAwait(false);
        if (tokens == null)
        {
            return null;
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            await _rateLimiter.WaitAsync(ct).ConfigureAwait(false);
            var client = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.spotify.com/v1" + relativePath);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retryAfter = 5;
                if (response.Headers.RetryAfter?.Delta != null)
                {
                    retryAfter = (int)Math.Ceiling(response.Headers.RetryAfter.Delta.Value.TotalSeconds);
                }
                else if (response.Headers.TryGetValues("Retry-After", out var values) &&
                         int.TryParse(values.FirstOrDefault(), out var parsed))
                {
                    retryAfter = parsed;
                }

                _rateLimiter.NotifyRateLimited(retryAfter);
                continue;
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                tokens = await _auth.GetValidTokensAsync(userId, ct).ConfigureAwait(false);
                if (tokens == null)
                {
                    return null;
                }

                continue;
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                _logger.LogInformation("Spotify API {Path} is not readable (403); skipping", relativePath);
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Spotify API {Path} failed: {Status} {Body}", relativePath, response.StatusCode, body);
                return null;
            }

            // Use plugin cache TTL days for long-lived objects; short TTL callers still benefit from in-memory reuse via store.
            await _cache.SetAsync(cacheKey, body, ct).ConfigureAwait(false);
            _ = cacheTtl;
            return body;
        }

        return null;
    }

    private static SpotifyPlaylistInfo ParsePlaylist(JsonElement el)
    {
        var info = new SpotifyPlaylistInfo
        {
            Id = el.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty : string.Empty,
            Name = el.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty,
            Description = el.TryGetProperty("description", out var desc) ? desc.GetString() : null,
            SnapshotId = el.TryGetProperty("snapshot_id", out var snap) ? snap.GetString() : null,
            Collaborative = el.TryGetProperty("collaborative", out var coll) && coll.GetBoolean(),
            ImageUrl = GetBestImage(el)
        };

        if (el.TryGetProperty("tracks", out var tracks) && tracks.TryGetProperty("total", out var total))
        {
            info.TrackCount = total.GetInt32();
        }
        else if (el.TryGetProperty("items", out var items) && items.TryGetProperty("total", out var total2))
        {
            info.TrackCount = total2.GetInt32();
        }

        if (el.TryGetProperty("owner", out var owner) && owner.TryGetProperty("id", out var oid))
        {
            info.OwnerId = oid.GetString();
        }

        return info;
    }

    public static SpotifyTrackInfo ParseTrack(JsonElement el)
    {
        var track = new SpotifyTrackInfo
        {
            Id = el.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty : string.Empty,
            Name = el.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty,
            DurationMs = el.TryGetProperty("duration_ms", out var dur) ? dur.GetInt32() : 0,
            TrackNumber = el.TryGetProperty("track_number", out var tn) ? tn.GetInt32() : 0,
            DiscNumber = el.TryGetProperty("disc_number", out var dn) ? dn.GetInt32() : 1
        };

        if (el.TryGetProperty("artists", out var artists))
        {
            foreach (var a in artists.EnumerateArray())
            {
                if (a.TryGetProperty("name", out var an) && an.GetString() is { } artistName)
                {
                    track.Artists.Add(artistName);
                }
            }
        }

        if (el.TryGetProperty("album", out var album))
        {
            track.Album = album.TryGetProperty("name", out var albumName) ? albumName.GetString() ?? string.Empty : string.Empty;
            track.AlbumId = album.TryGetProperty("id", out var albumId) ? albumId.GetString() : null;
            track.CoverUrl = GetBestImage(album);
            if (album.TryGetProperty("artists", out var albumArtists) && albumArtists.GetArrayLength() > 0)
            {
                track.AlbumArtist = albumArtists[0].GetProperty("name").GetString();
            }

            if (album.TryGetProperty("release_date", out var rd))
            {
                var s = rd.GetString();
                if (!string.IsNullOrEmpty(s) && s.Length >= 4 && int.TryParse(s[..4], out var y))
                {
                    track.Year = y;
                }
            }
        }

        if (el.TryGetProperty("external_ids", out var ext) && ext.TryGetProperty("isrc", out var isrc))
        {
            track.Isrc = isrc.GetString();
        }

        track.AlbumArtist ??= track.Artists.FirstOrDefault();
        return track;
    }

    private static string? GetBestImage(JsonElement el)
    {
        if (!el.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array || images.GetArrayLength() == 0)
        {
            return null;
        }

        return images[0].TryGetProperty("url", out var url) ? url.GetString() : null;
    }
}
