using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        var json = await GetStringAsync(userId, path, $"search:{types}:{query}:{limit}:{offset}", TimeSpan.FromHours(1), ct).ConfigureAwait(false);
        if (json == null)
        {
            return null;
        }

        var tokens = await _auth.GetValidTokensAsync(userId, ct).ConfigureAwait(false);
        return ExcludeForeignSearchPlaylists(json, tokens?.SpotifyUserId);
    }

    public async Task<IReadOnlyList<SpotifyPlaylistInfo>> GetUserPlaylistsAsync(Guid userId, CancellationToken ct = default, int maxItems = int.MaxValue)
    {
        var tokens = await _auth.GetValidTokensAsync(userId, ct).ConfigureAwait(false);
        var spotifyUserId = tokens?.SpotifyUserId;
        var playlists = new List<SpotifyPlaylistInfo>();
        var offset = 0;
        while (playlists.Count < maxItems)
        {
            var path = $"/me/playlists?limit=50&offset={offset}";
            var json = await GetStringAsync(userId, path, $"me:playlists:{offset}", TimeSpan.FromHours(6), ct).ConfigureAwait(false);
            if (json == null)
            {
                break;
            }

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            {
                break;
            }

            var pageCount = 0;
            foreach (var item in items.EnumerateArray())
            {
                pageCount++;
                var playlist = ParsePlaylist(item);
                MarkPlaylistAccess(playlist, spotifyUserId);
                if (!CanReadPlaylistItems(playlist))
                {
                    continue;
                }

                playlists.Add(playlist);
                if (playlists.Count >= maxItems)
                {
                    break;
                }
            }

            if (playlists.Count >= maxItems || pageCount == 0 || !doc.RootElement.TryGetProperty("next", out var next) || next.ValueKind == JsonValueKind.Null)
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
        playlistId = NormalizePlaylistId(playlistId);
        if (string.IsNullOrEmpty(playlistId))
        {
            return null;
        }

        var metaJson = await GetStringAsync(
                userId,
                $"/playlists/{Uri.EscapeDataString(playlistId)}",
                $"playlist-v6:{playlistId}",
                TimeSpan.Zero,
                ct)
            .ConfigureAwait(false);
        if (metaJson == null)
        {
            return null;
        }

        using var metaDoc = JsonDocument.Parse(metaJson);
        var playlist = ParsePlaylist(metaDoc.RootElement);
        var tokens = await _auth.GetValidTokensAsync(userId, ct).ConfigureAwait(false);
        MarkPlaylistAccess(playlist, tokens?.SpotifyUserId);
        var tracks = new List<SpotifyTrackInfo>();
        if (!CanReadPlaylistItems(playlist))
        {
            playlist.ItemsRestricted = true;
            return (playlist, tracks);
        }

        AddTracksFromPage(metaDoc.RootElement, tracks);
        var next = tracks.Count == 0 ? PlaylistItemsPath(playlistId, 0) : ReadNext(ResolvePaging(metaDoc.RootElement));
        string? itemsError = null;
        if (tracks.Count == 0 || !string.IsNullOrWhiteSpace(next))
        {
            itemsError = await FollowTrackPagesAsync(
                    userId,
                    playlistId,
                    next ?? PlaylistItemsPath(playlistId, 0),
                    tracks,
                    ct)
                .ConfigureAwait(false);
        }

        playlist.ItemsRestricted = tracks.Count == 0;
        playlist.ItemsError = itemsError;
        if (tracks.Count == 0)
        {
            LogUnparsedPlaylistPage(playlistId, metaDoc.RootElement);
            _logger.LogWarning(
                "Playlist {PlaylistId} returned no tracks. Restricted={Restricted} Error={Error}",
                playlistId,
                playlist.ItemsRestricted,
                itemsError);
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
                var trackEl = UnwrapTrackElement(item);
                if (trackEl.ValueKind != JsonValueKind.Object)
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
        if (string.IsNullOrWhiteSpace(trackId))
        {
            return null;
        }

        var json = await GetStringAsync(
                userId,
                $"/tracks/{Uri.EscapeDataString(trackId.Trim())}",
                $"track:{trackId.Trim()}",
                TimeSpan.FromDays(10),
                ct)
            .ConfigureAwait(false);
        if (json == null)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(json);
        var track = ParseTrack(doc.RootElement);
        return string.IsNullOrEmpty(track.Id) ? null : track;
    }

    public async Task<IReadOnlyDictionary<string, SpotifyTrackInfo>> GetTracksAsync(
        Guid userId,
        IEnumerable<string> trackIds,
        CancellationToken ct = default)
    {
        var found = new Dictionary<string, SpotifyTrackInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in trackIds
                     .Where(id => !string.IsNullOrWhiteSpace(id))
                     .Select(id => id.Trim())
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var track = await GetTrackAsync(userId, id, ct).ConfigureAwait(false);
            if (track != null)
            {
                found[track.Id] = track;
            }
        }

        return found;
    }

    public async Task<IReadOnlyList<SpotifyTrackInfo>> GetAlbumTracksAsync(Guid userId, string albumId, CancellationToken ct = default)
    {
        var albumJson = await GetStringAsync(userId, $"/albums/{Uri.EscapeDataString(albumId)}", $"album-v2:{albumId}", TimeSpan.FromDays(10), ct)
            .ConfigureAwait(false);
        string? albumName = null;
        string? albumArtist = null;
        string? cover = null;
        int? year = null;
        if (albumJson != null)
        {
            using var albumDoc = JsonDocument.Parse(albumJson);
            albumName = albumDoc.RootElement.TryGetProperty("name", out var albumNameEl)
                ? albumNameEl.GetString()
                : null;
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
            var path = $"/albums/{Uri.EscapeDataString(albumId)}/tracks?limit=50&offset={offset}";
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
                if (string.IsNullOrEmpty(track.Id))
                {
                    continue;
                }

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

    public async Task<(int Total, IReadOnlyList<SpotifyTrackInfo> Preview)> GetLikedPreviewAsync(
        Guid userId,
        int previewCount,
        CancellationToken ct = default)
    {
        var limit = Math.Clamp(previewCount, 1, 50);
        var json = await GetStringAsync(
                userId,
                $"/me/tracks?limit={limit}&offset=0",
                $"me:tracks-preview:{limit}",
                TimeSpan.FromHours(6),
                ct)
            .ConfigureAwait(false);
        if (json == null)
        {
            return (0, []);
        }

        using var doc = JsonDocument.Parse(json);
        var total = doc.RootElement.TryGetProperty("total", out var totalEl) && totalEl.ValueKind == JsonValueKind.Number
            ? totalEl.GetInt32()
            : 0;
        var tracks = new List<SpotifyTrackInfo>();
        if (doc.RootElement.TryGetProperty("items", out var items))
        {
            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("track", out var trackEl) || trackEl.ValueKind != JsonValueKind.Object)
                {
                    trackEl = UnwrapTrackElement(item);
                }

                if (trackEl.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var track = ParseTrack(trackEl);
                if (!string.IsNullOrEmpty(track.Id))
                {
                    tracks.Add(track);
                }
            }
        }

        return (total, tracks);
    }

    public async Task<IReadOnlyList<SpotifyAlbumInfo>> GetSavedAlbumsAsync(Guid userId, int maxItems, CancellationToken ct = default)
    {
        var albums = new List<SpotifyAlbumInfo>();
        var offset = 0;
        while (albums.Count < maxItems)
        {
            var json = await GetStringAsync(
                    userId,
                    $"/me/albums?limit=50&offset={offset}",
                    $"me:albums:{offset}",
                    TimeSpan.FromHours(6),
                    ct)
                .ConfigureAwait(false);
            if (json == null)
            {
                break;
            }

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("items", out var items) || items.GetArrayLength() == 0)
            {
                break;
            }

            foreach (var item in items.EnumerateArray())
            {
                var albumEl = item.TryGetProperty("album", out var nested) ? nested : item;
                var album = ParseAlbum(albumEl);
                if (!string.IsNullOrEmpty(album.Id))
                {
                    albums.Add(album);
                }

                if (albums.Count >= maxItems)
                {
                    break;
                }
            }

            if (!doc.RootElement.TryGetProperty("next", out var next) || next.ValueKind == JsonValueKind.Null)
            {
                break;
            }

            offset += 50;
        }

        return albums;
    }

    public async Task<IReadOnlyList<SpotifyArtistInfo>> GetFollowedArtistsAsync(Guid userId, int maxItems, CancellationToken ct = default)
    {
        var artists = new List<SpotifyArtistInfo>();
        string? next = $"/me/following?type=artist&limit=50";
        var pages = 0;
        while (!string.IsNullOrEmpty(next) && artists.Count < maxItems && pages < 20)
        {
            pages++;
            var json = await GetStringAsync(
                    userId,
                    NormalizeSpotifyPath(next),
                    $"me:following:{pages}",
                    TimeSpan.FromHours(6),
                    ct)
                .ConfigureAwait(false);
            if (json == null)
            {
                break;
            }

            using var doc = JsonDocument.Parse(json);
            var page = doc.RootElement.TryGetProperty("artists", out var artistsEl) ? artistsEl : doc.RootElement;
            if (!page.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            {
                break;
            }

            foreach (var item in items.EnumerateArray())
            {
                var artist = ParseArtist(item);
                if (!string.IsNullOrEmpty(artist.Id))
                {
                    artists.Add(artist);
                }

                if (artists.Count >= maxItems)
                {
                    break;
                }
            }

            next = ReadNext(page);
        }

        return artists;
    }

    public async Task<IReadOnlyList<SpotifyTrackInfo>> GetTopTracksAsync(
        Guid userId,
        int limit = 50,
        string timeRange = "medium_term",
        CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 50);
        if (timeRange is not ("short_term" or "medium_term" or "long_term"))
        {
            timeRange = "medium_term";
        }

        var json = await GetStringAsync(
                userId,
                $"/me/top/tracks?time_range={Uri.EscapeDataString(timeRange)}&limit={limit}",
                $"me:top:tracks:{timeRange}:{limit}",
                TimeSpan.Zero,
                ct)
            .ConfigureAwait(false);
        return ParsePagingTracks(json);
    }

    public async Task<IReadOnlyList<SpotifyArtistInfo>> GetTopArtistsAsync(
        Guid userId,
        int limit = 50,
        string timeRange = "medium_term",
        CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 50);
        if (timeRange is not ("short_term" or "medium_term" or "long_term"))
        {
            timeRange = "medium_term";
        }

        var json = await GetStringAsync(
                userId,
                $"/me/top/artists?time_range={Uri.EscapeDataString(timeRange)}&limit={limit}",
                $"me:top:artists:{timeRange}:{limit}",
                TimeSpan.Zero,
                ct)
            .ConfigureAwait(false);
        var artists = new List<SpotifyArtistInfo>();
        if (json == null)
        {
            return artists;
        }

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return artists;
        }

        foreach (var item in items.EnumerateArray())
        {
            var artist = ParseArtist(item);
            if (!string.IsNullOrEmpty(artist.Id))
            {
                artists.Add(artist);
            }
        }

        return artists;
    }

    public async Task<IReadOnlyList<SpotifyTrackInfo>> GetRecentlyPlayedAsync(Guid userId, int limit = 50, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 50);
        var json = await GetStringAsync(
                userId,
                $"/me/player/recently-played?limit={limit}",
                "me:recently-played",
                TimeSpan.Zero,
                ct)
            .ConfigureAwait(false);
        var tracks = new List<SpotifyTrackInfo>();
        if (json == null)
        {
            return tracks;
        }

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return tracks;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items.EnumerateArray())
        {
            var trackEl = UnwrapTrackElement(item);
            if (trackEl.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var track = ParseTrack(trackEl);
            if (string.IsNullOrEmpty(track.Id) || !seen.Add(track.Id))
            {
                continue;
            }

            tracks.Add(track);
        }

        return tracks;
    }

    private static IReadOnlyList<SpotifyTrackInfo> ParsePagingTracks(string? json)
    {
        var tracks = new List<SpotifyTrackInfo>();
        if (json == null)
        {
            return tracks;
        }

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return tracks;
        }

        foreach (var item in items.EnumerateArray())
        {
            var trackEl = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("type", out var type) && type.GetString() == "track"
                ? item
                : UnwrapTrackElement(item);
            if (trackEl.ValueKind != JsonValueKind.Object)
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    trackEl = item;
                }
                else
                {
                    continue;
                }
            }

            var track = ParseTrack(trackEl);
            if (!string.IsNullOrEmpty(track.Id))
            {
                tracks.Add(track);
            }
        }

        return tracks;
    }

    public async Task<SpotifyArtistInfo?> GetArtistAsync(Guid userId, string artistId, CancellationToken ct = default)
    {
        var json = await GetStringAsync(
                userId,
                $"/artists/{Uri.EscapeDataString(artistId)}",
                $"artist:{artistId}",
                TimeSpan.FromDays(10),
                ct)
            .ConfigureAwait(false);
        if (json == null)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(json);
        var artist = ParseArtist(doc.RootElement);
        return string.IsNullOrEmpty(artist.Id) ? null : artist;
    }

    public Task<IReadOnlyList<SpotifyTrackInfo>> GetArtistTopTracksAsync(Guid userId, string artistId, CancellationToken ct = default)
    {
        _ = (userId, artistId, ct);
        return Task.FromResult<IReadOnlyList<SpotifyTrackInfo>>([]);
    }

    public async Task<IReadOnlyList<SpotifyAlbumInfo>> GetArtistAlbumsAsync(Guid userId, string artistId, CancellationToken ct = default)
    {
        var albums = new List<SpotifyAlbumInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var offset = 0;
        while (true)
        {
            var path = $"/artists/{Uri.EscapeDataString(artistId)}/albums?include_groups=album,single&limit=10&offset={offset}";
            var json = await GetStringAsync(userId, path, $"artist-albums:{artistId}:{offset}", TimeSpan.FromDays(10), ct)
                .ConfigureAwait(false);
            if (json == null)
            {
                break;
            }

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("items", out var items) || items.GetArrayLength() == 0)
            {
                break;
            }

            foreach (var item in items.EnumerateArray())
            {
                var album = ParseAlbum(item);
                if (string.IsNullOrEmpty(album.Id) || !seen.Add(album.Id))
                {
                    continue;
                }

                albums.Add(album);
            }

            if (!doc.RootElement.TryGetProperty("next", out var next) || next.ValueKind == JsonValueKind.Null)
            {
                break;
            }

            offset += 10;
        }

        return albums;
    }

    private async Task<string?> GetStringAsync(
        Guid userId,
        string relativePath,
        string cacheKey,
        TimeSpan cacheTtl,
        CancellationToken ct)
    {
        var (json, _) = await GetJsonAsync(userId, relativePath, cacheKey, cacheTtl, ct).ConfigureAwait(false);
        return json;
    }

    private async Task<(string? Json, string? Error)> GetJsonAsync(
        Guid userId,
        string relativePath,
        string cacheKey,
        TimeSpan cacheTtl,
        CancellationToken ct)
    {
        var scopedKey = userId.ToString("N") + ":" + cacheKey;
        if (cacheTtl > TimeSpan.Zero)
        {
            var cached = await _cache.GetAsync(scopedKey, ct).ConfigureAwait(false);
            if (cached != null)
            {
                return (cached, null);
            }
        }

        var tokens = await _auth.GetValidTokensAsync(userId, ct).ConfigureAwait(false);
        if (tokens == null)
        {
            return (null, "Spotify is not linked for this Jellyfin user.");
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            await _rateLimiter.WaitAsync(ct).ConfigureAwait(false);
            var client = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, ToSpotifyUrl(relativePath));
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
                    return (null, "Spotify login expired. Link Spotify again from Sync.");
                }

                continue;
            }

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return (null, ReadSpotifyError(body) ?? "Not found");
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                var error = ReadSpotifyError(body) ?? "Forbidden";
                _logger.LogInformation("Spotify API {Path} is not readable (403): {Error}", relativePath, error);
                return (null, error);
            }

            if (!response.IsSuccessStatusCode)
            {
                var error = ReadSpotifyError(body) ?? response.StatusCode.ToString();
                _logger.LogWarning("Spotify API {Path} failed: {Status} {Body}", relativePath, response.StatusCode, body);
                return (null, error);
            }

            if (cacheTtl > TimeSpan.Zero && !IsHollowTrackPage(body))
            {
                await _cache.SetAsync(scopedKey, body, ct).ConfigureAwait(false);
            }

            return (body, null);
        }

        return (null, "Spotify rate limit, try again in a moment.");
    }

    private static string? ReadSpotifyError(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.Object &&
                    error.TryGetProperty("message", out var message) &&
                    message.ValueKind == JsonValueKind.String)
                {
                    return message.GetString();
                }

                if (error.ValueKind == JsonValueKind.String)
                {
                    return error.GetString();
                }
            }
        }
        catch (JsonException)
        {
        }

        return body.Trim();
    }

    private async Task<string?> FollowTrackPagesAsync(
        Guid userId,
        string playlistId,
        string? next,
        List<SpotifyTrackInfo> tracks,
        CancellationToken ct)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pages = 0;
        string? lastError = null;
        while (!string.IsNullOrWhiteSpace(next) && pages < 200)
        {
            if (!seen.Add(next))
            {
                break;
            }

            pages++;
            var (json, error) = await GetJsonAsync(
                    userId,
                    NormalizePlaylistItemsPath(next),
                    $"playlist-items-v6:{playlistId}:{pages}",
                    TimeSpan.Zero,
                    ct)
                .ConfigureAwait(false);
            if (json == null)
            {
                lastError = error;
                break;
            }

            using var doc = JsonDocument.Parse(json);
            var paging = ResolvePaging(doc.RootElement);
            var before = tracks.Count;
            AddTracksFromPage(paging, tracks);
            if (tracks.Count == before)
            {
                LogUnparsedPlaylistPage(playlistId, doc.RootElement);
            }

            next = ReadNext(paging);
            if (tracks.Count == before && string.IsNullOrWhiteSpace(next))
            {
                break;
            }
        }

        return lastError;
    }

    private void LogUnparsedPlaylistPage(string playlistId, JsonElement root)
    {
        var paging = ResolvePaging(root);
        if (!paging.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0)
        {
            return;
        }

        _logger.LogWarning(
            "Playlist {PlaylistId} page had {Count} entries but none parsed. First keys: {Keys}",
            playlistId,
            items.GetArrayLength(),
            DescribeKeys(items[0]));
    }

    private static void AddTracksFromPage(JsonElement page, List<SpotifyTrackInfo> tracks)
    {
        var paging = ResolvePaging(page);
        if (!paging.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in items.EnumerateArray())
        {
            var trackEl = UnwrapTrackElement(item);
            if (trackEl.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (trackEl.TryGetProperty("type", out var type) && type.GetString() is { } typeName && typeName != "track")
            {
                continue;
            }

            var track = ParseTrack(trackEl);
            if (!string.IsNullOrEmpty(track.Id))
            {
                tracks.Add(track);
            }
        }
    }

    private static JsonElement UnwrapTrackElement(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return default;
        }

        if (item.TryGetProperty("item", out var wrapped) && wrapped.ValueKind == JsonValueKind.Object)
        {
            return wrapped;
        }

        if (item.TryGetProperty("track", out var nested) && nested.ValueKind == JsonValueKind.Object)
        {
            return nested;
        }

        if (item.TryGetProperty("type", out var directType) && directType.GetString() == "track")
        {
            return item;
        }

        if (item.TryGetProperty("uri", out var uri) && (uri.GetString() ?? string.Empty).StartsWith("spotify:track:", StringComparison.OrdinalIgnoreCase))
        {
            return item;
        }

        return default;
    }

    private static string? ReadNext(JsonElement page)
    {
        if (!page.TryGetProperty("next", out var next) || next.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = next.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static JsonElement ResolvePaging(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return root;
        }

        if (root.TryGetProperty("items", out var items))
        {
            if (items.ValueKind == JsonValueKind.Array)
            {
                return root;
            }

            if (items.ValueKind == JsonValueKind.Object)
            {
                return items.TryGetProperty("items", out var nested) && nested.ValueKind == JsonValueKind.Array
                    ? items
                    : items;
            }
        }

        if (root.TryGetProperty("tracks", out var tracks) && tracks.ValueKind == JsonValueKind.Object)
        {
            return ResolvePaging(tracks);
        }

        return root;
    }

    private static string DescribeKeys(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object)
        {
            return el.ValueKind.ToString();
        }

        return string.Join(',', el.EnumerateObject().Select(prop => prop.Name));
    }

    private static string PlaylistItemsPath(string playlistId, int offset)
    {
        return $"/playlists/{Uri.EscapeDataString(playlistId)}/items?limit=50&offset={offset}";
    }

    private static string NormalizePlaylistItemsPath(string pathOrUrl)
    {
        var value = StripPlaylistTrackQuery(NormalizeSpotifyPath(pathOrUrl));
        var queryAt = value.IndexOf('?');
        var pathPart = queryAt >= 0 ? value[..queryAt] : value;
        var query = queryAt >= 0 ? value[queryAt..] : string.Empty;
        if (pathPart.EndsWith("/tracks", StringComparison.OrdinalIgnoreCase))
        {
            pathPart = string.Concat(pathPart.AsSpan(0, pathPart.Length - "/tracks".Length), "/items");
        }

        return pathPart + query;
    }

    private static string StripPlaylistTrackQuery(string path)
    {
        var value = path;
        foreach (var key in new[] { "market", "additional_types" })
        {
            value = System.Text.RegularExpressions.Regex.Replace(
                value,
                @"([?&])" + key + @"=[^&]*",
                "$1",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        value = value.Replace("?&", "?", StringComparison.Ordinal);
        value = value.TrimEnd('?', '&');
        return value;
    }

    private static string NormalizePlaylistId(string playlistId)
    {
        var value = (playlistId ?? string.Empty).Trim();
        if (value.StartsWith("spotify:playlist:", StringComparison.OrdinalIgnoreCase))
        {
            value = value["spotify:playlist:".Length..];
        }

        if (value.Contains("/playlist/", StringComparison.OrdinalIgnoreCase))
        {
            var start = value.LastIndexOf("/playlist/", StringComparison.OrdinalIgnoreCase);
            value = value[(start + "/playlist/".Length)..];
        }

        var query = value.IndexOf('?');
        if (query >= 0)
        {
            value = value[..query];
        }

        return value.Trim();
    }

    private static string NormalizeSpotifyPath(string pathOrUrl)
    {
        const string prefix = "https://api.spotify.com/v1";
        if (pathOrUrl.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return pathOrUrl[prefix.Length..];
        }

        return pathOrUrl.StartsWith('/') ? pathOrUrl : "/" + pathOrUrl;
    }

    private static string ToSpotifyUrl(string pathOrUrl)
    {
        if (pathOrUrl.StartsWith("https://api.spotify.com/", StringComparison.OrdinalIgnoreCase))
        {
            return pathOrUrl;
        }

        return "https://api.spotify.com/v1" + NormalizeSpotifyPath(pathOrUrl);
    }

    private static bool IsHollowTrackPage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var paging = ResolvePaging(doc.RootElement);
            if (!paging.TryGetProperty("total", out var totalEl) || totalEl.ValueKind != JsonValueKind.Number)
            {
                return false;
            }

            var total = totalEl.GetInt32();
            if (total <= 0 || !paging.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            return items.GetArrayLength() == 0;
        }
        catch (JsonException)
        {
            return false;
        }
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

        if (el.TryGetProperty("tracks", out var tracks)
            && tracks.ValueKind == JsonValueKind.Object
            && tracks.TryGetProperty("total", out var total)
            && total.ValueKind == JsonValueKind.Number)
        {
            info.TrackCount = total.GetInt32();
        }
        else if (el.TryGetProperty("items", out var itemsPaging)
                 && itemsPaging.ValueKind == JsonValueKind.Object
                 && itemsPaging.TryGetProperty("total", out var total2)
                 && total2.ValueKind == JsonValueKind.Number)
        {
            info.TrackCount = total2.GetInt32();
        }

        if (el.TryGetProperty("owner", out var owner) && owner.ValueKind == JsonValueKind.Object)
        {
            if (owner.TryGetProperty("id", out var oid))
            {
                info.OwnerId = oid.GetString();
            }

            if (owner.TryGetProperty("display_name", out var ownerName) && ownerName.ValueKind == JsonValueKind.String)
            {
                info.OwnerName = ownerName.GetString();
            }

            info.OwnerName ??= info.OwnerId;
        }

        return info;
    }

    private static void MarkPlaylistAccess(SpotifyPlaylistInfo playlist, string? spotifyUserId)
    {
        playlist.Owned = !string.IsNullOrEmpty(spotifyUserId)
            && !string.IsNullOrEmpty(playlist.OwnerId)
            && string.Equals(playlist.OwnerId, spotifyUserId, StringComparison.Ordinal);
    }

    private static bool CanReadPlaylistItems(SpotifyPlaylistInfo playlist)
    {
        return playlist.Owned || playlist.Collaborative;
    }

    private static string ExcludeForeignSearchPlaylists(string json, string? spotifyUserId)
    {
        try
        {
            var root = JsonNode.Parse(json);
            var items = root?["playlists"]?["items"] as JsonArray;
            if (root == null || items == null)
            {
                return json;
            }

            for (var i = items.Count - 1; i >= 0; i--)
            {
                var ownerId = items[i]?["owner"]?["id"]?.GetValue<string>();
                if (string.IsNullOrEmpty(spotifyUserId) ||
                    !string.Equals(ownerId, spotifyUserId, StringComparison.Ordinal))
                {
                    items.RemoveAt(i);
                }
            }

            return root.ToJsonString();
        }
        catch (JsonException)
        {
            return json;
        }
    }

    public static SpotifyTrackInfo ParseTrack(JsonElement el)
    {
        var track = new SpotifyTrackInfo
        {
            Id = ReadTrackId(el),
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
                    track.ArtistIds.Add(a.TryGetProperty("id", out var aid) ? aid.GetString() ?? string.Empty : string.Empty);
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

    private static string ReadTrackId(JsonElement el)
    {
        if (el.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(id.GetString()))
        {
            return id.GetString() ?? string.Empty;
        }

        if (el.TryGetProperty("uri", out var uriEl) && uriEl.ValueKind == JsonValueKind.String)
        {
            var uri = uriEl.GetString() ?? string.Empty;
            const string prefix = "spotify:track:";
            if (uri.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return uri[prefix.Length..];
            }
        }

        return string.Empty;
    }

    private static SpotifyAlbumInfo ParseAlbum(JsonElement el)
    {
        var album = new SpotifyAlbumInfo
        {
            Id = el.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty : string.Empty,
            Name = el.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty,
            ImageUrl = GetBestImage(el),
            AlbumType = el.TryGetProperty("album_type", out var type) ? type.GetString() : null
        };

        if (el.TryGetProperty("total_tracks", out var total) && total.ValueKind == JsonValueKind.Number)
        {
            album.TrackCount = total.GetInt32();
        }

        if (el.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in artists.EnumerateArray())
            {
                if (a.TryGetProperty("name", out var an) && an.GetString() is { } artistName)
                {
                    album.Artists.Add(artistName);
                }
            }
        }

        if (el.TryGetProperty("release_date", out var rd))
        {
            var s = rd.GetString();
            if (!string.IsNullOrEmpty(s) && s.Length >= 4 && int.TryParse(s[..4], out var y))
            {
                album.Year = y;
            }
        }

        return album;
    }

    private static SpotifyArtistInfo ParseArtist(JsonElement el)
    {
        var artist = new SpotifyArtistInfo
        {
            Id = el.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty : string.Empty,
            Name = el.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty,
            ImageUrl = GetBestImage(el)
        };

        if (el.TryGetProperty("genres", out var genres) && genres.ValueKind == JsonValueKind.Array)
        {
            foreach (var g in genres.EnumerateArray())
            {
                if (g.GetString() is { } genre)
                {
                    artist.Genres.Add(genre);
                }
            }
        }

        if (el.TryGetProperty("followers", out var followers) && followers.TryGetProperty("total", out var total)
            && total.ValueKind == JsonValueKind.Number)
        {
            artist.Followers = total.GetInt32();
        }

        return artist;
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
