using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Jellyfin.Plugin.JellySpot.Configuration;
using Jellyfin.Plugin.JellySpot.Helpers;
using Jellyfin.Plugin.JellySpot.Models;
using Jellyfin.Plugin.JellySpot.Services;
using Jellyfin.Plugin.JellySpot.Services.Download;
using Jellyfin.Plugin.JellySpot.Services.Spotify;
using Jellyfin.Plugin.JellySpot.Services.Storage;
using Jellyfin.Plugin.JellySpot.Services.Sync;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellySpot.Controllers;

[ApiController]
[JellySpotExceptionFilter]
[Route("JellySpot")]
public partial class JellySpotController : ControllerBase
{
    private BrowserAvailabilityService? _availability;
    private BrowserAvailabilityService Availability => _availability ??= new(_store, _libraryManager, _userManager, _storage);
    private readonly SpotifyAuthService _auth;
    private readonly SpotifyApiClient _spotify;
    private readonly JellySpotStore _store;
    private readonly SyncEngine _sync;
    private readonly DownloadQueueService _queue;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IApplicationPaths _applicationPaths;
    private readonly FfmpegLocator _ffmpeg;
    private readonly LibraryStorage _storage;
    private readonly ILogger<JellySpotController> _logger;

    public JellySpotController(
        SpotifyAuthService auth,
        SpotifyApiClient spotify,
        JellySpotStore store,
        SyncEngine sync,
        DownloadQueueService queue,
        IUserManager userManager,
        ILibraryManager libraryManager,
        IApplicationPaths applicationPaths,
        FfmpegLocator ffmpeg,
        LibraryStorage storage,
        ILogger<JellySpotController> logger,
        BrowserAvailabilityService? availability = null)
    {
        _availability = availability;
        _auth = auth;
        _spotify = spotify;
        _store = store;
        _sync = sync;
        _queue = queue;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _applicationPaths = applicationPaths;
        _ffmpeg = ffmpeg;
        _storage = storage;
        _logger = logger;
    }

    [HttpGet("jellyspot-tabs.js")]
    public ActionResult GetTabsScript() => ServeBrowserResources("application/javascript", "Inject.jellyspot-tabs.js", "Inject.jellyspot-browser.js");

    [HttpGet("jellyspot-tabs.css")]
    public ActionResult GetTabsStylesheet() => ServeBrowserResources("text/css", "Inject.jellyspot-tabs.css", "Inject.jellyspot-browser.css");

    [HttpGet("Configuration/config.css")]
    public ActionResult GetConfigStylesheet() => ServeEmbedded("Configuration.config.css", "text/css");

    [HttpGet("admin/health")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult GetHealth()
    {
        var libraries = LibraryCatalog.ListLibraries(_libraryManager, _logger);
        var storageRoot = LibraryCatalog.ResolveStorageRoot(_libraryManager, _logger);
        var storageExists = !string.IsNullOrWhiteSpace(storageRoot) && Directory.Exists(storageRoot);
        var storageWritable = false;
        if (storageExists)
        {
            try
            {
                var probe = Path.Combine(storageRoot!, ".jellyspot-write-probe");
                System.IO.File.WriteAllText(probe, "ok");
                System.IO.File.Delete(probe);
                storageWritable = true;
            }
            catch
            {
                storageWritable = false;
            }
        }

        return Ok(new
        {
            fileTransformation = FileTransformationHelper.IsPresent(_applicationPaths),
            ffmpegReady = _ffmpeg.IsReady,
            ffmpegPath = _ffmpeg.EncoderPath,
            ffmpegVersion = _ffmpeg.EncoderVersion,
            ffmpegSource = _ffmpeg.Source,
            storageRoot,
            storageExists,
            storageWritable,
            legacyPlaylistExports = string.IsNullOrWhiteSpace(storageRoot) || !Directory.Exists(Path.Combine(storageRoot, "Playlists"))
                ? Array.Empty<string>() : Directory.GetFiles(Path.Combine(storageRoot, "Playlists"), "*.m3u8", SearchOption.TopDirectoryOnly),
            libraries = libraries.Select(l => new
            {
                id = l.Id,
                name = l.Name,
                collectionType = l.CollectionType,
                locations = l.Locations
            })
        });
    }

    [HttpGet("Configuration")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult<PluginConfiguration> GetConfiguration()
    {
        return Plugin.Instance!.Configuration;
    }

    [HttpPost("Configuration")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult UpdateConfiguration([FromBody] PluginConfiguration config)
    {
        var current = Plugin.Instance!.Configuration;
        current.StorageRootPath = config.StorageRootPath?.Trim() ?? string.Empty;
        current.SelectedLibraryId = config.SelectedLibraryId?.Trim() ?? string.Empty;
        current.SpotifyClientId = config.SpotifyClientId?.Trim() ?? string.Empty;
        current.SpotifyClientSecret = config.SpotifyClientSecret?.Trim() ?? string.Empty;
        current.SpotifyRedirectUri = string.IsNullOrWhiteSpace(config.SpotifyRedirectUri)
            ? current.SpotifyRedirectUri
            : config.SpotifyRedirectUri.Trim();
        current.SpotifyRequestsPerSecond = Math.Clamp(config.SpotifyRequestsPerSecond, 0.2, 10);
        current.SpotifyMaxConcurrentRequests = Math.Clamp(config.SpotifyMaxConcurrentRequests, 1, 8);
        current.DownloadConcurrency = Math.Clamp(config.DownloadConcurrency, 1, 8);
        current.PreferredFormat = string.IsNullOrWhiteSpace(config.PreferredFormat) ? "m4a" : config.PreferredFormat.Trim('.').ToLowerInvariant();
        current.AudioQuality = NormalizeAudioQuality(config.AudioQuality);
        current.MinMatchScore = Math.Clamp(config.MinMatchScore, 50, 100);
        current.SyncIntervalMinutes = Math.Max(15, config.SyncIntervalMinutes);
        current.TriggerLibraryRefresh = config.TriggerLibraryRefresh;
        current.CacheTtlDays = Math.Clamp(config.CacheTtlDays, 1, 30);
        current.FfmpegPath = config.FfmpegPath?.Trim() ?? string.Empty;
        Plugin.Instance.SaveConfiguration();
        return NoContent();
    }

    [HttpGet("Me")]
    [Authorize]
    public async Task<ActionResult> GetMe(CancellationToken ct)
    {
        var userId = GetUserId();
        var tokens = await _store.GetTokensAsync(userId, ct).ConfigureAwait(false);
        var settings = await _store.GetUserSettingsAsync(userId, ct).ConfigureAwait(false);
        return Ok(new
        {
            Linked = tokens != null && !tokens.RelinkRequired,
            ConnectionState = tokens == null ? "unlinked" : tokens.RelinkRequired ? "relink_required" : "connected",
            ServerReady = !string.IsNullOrWhiteSpace(Plugin.Instance?.Configuration.SpotifyClientId),
            SpotifyDisplayName = tokens?.DisplayName,
            SpotifyUserId = tokens?.SpotifyUserId,
            Settings = settings
        });
    }

    [HttpGet("OAuth/Start")]
    [Authorize]
    public async Task<ActionResult> StartOAuth(CancellationToken ct)
    {
        var url = await _auth.CreateAuthorizationUrlAsync(GetUserId(), ct).ConfigureAwait(false);
        return Ok(new { Url = url });
    }

    [HttpGet("OAuth/Callback")]
    [AllowAnonymous]
    public async Task<ActionResult> OAuthCallback([FromQuery] string? code, [FromQuery] string? state, [FromQuery] string? error, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(error))
        {
            return Content($"<html><body><h2>Spotify authorization failed</h2><p>{System.Net.WebUtility.HtmlEncode(error)}</p></body></html>", "text/html");
        }

        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
        {
            return BadRequest("Missing code/state");
        }

        try
        {
            await _auth.HandleCallbackAsync(code, state, ct).ConfigureAwait(false);
            return Content("<html><body><h2>Spotify linked successfully</h2><p>You can close this window and return to JellySpot.</p></body></html>", "text/html");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OAuth callback failed");
            return Content("<html><body><h2>Link failed</h2><p>Please return to JellySpot and try linking again.</p></body></html>", "text/html");
        }
    }

    [HttpPost("OAuth/Unlink")]
    [Authorize]
    public async Task<ActionResult> Unlink(CancellationToken ct)
    {
        await _auth.UnlinkAsync(GetUserId(), ct).ConfigureAwait(false);
        return NoContent();
    }

    [HttpGet("Search")]
    [Authorize]
    public async Task<ActionResult> Search([FromQuery, Required] string q, [FromQuery] string type = "track,album,playlist", [FromQuery] int limit = 10, [FromQuery] int offset = 0, CancellationToken ct = default)
    {
        var result = await _spotify.SearchAsync(await ResolveLinkedUserIdAsync(ct).ConfigureAwait(false), q, type, limit, offset, ct).ConfigureAwait(false);
        if (result == null)
        {
            return Ok(new { });
        }

        return Content(result, "application/json");
    }

    [HttpGet("Playlists")]
    [Authorize]
    public async Task<ActionResult> Playlists(CancellationToken ct)
    {
        var playlists = await _spotify.GetUserPlaylistsAsync(await ResolveLinkedUserIdAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        return Ok(playlists);
    }

    [HttpGet("Playlists/{id}")]
    [Authorize]
    public async Task<ActionResult> Playlist(string id, CancellationToken ct)
    {
        var result = await _spotify.GetPlaylistWithTracksAsync(await ResolveLinkedUserIdAsync(ct).ConfigureAwait(false), id, ct).ConfigureAwait(false);
        if (result == null)
        {
            return NotFound();
        }

        return Ok(new
        {
            Playlist = result.Value.Playlist,
            Tracks = result.Value.Tracks,
            Restricted = result.Value.Playlist.ItemsRestricted,
            Owned = result.Value.Playlist.Owned,
            OwnerName = result.Value.Playlist.OwnerName,
            Error = result.Value.Playlist.ItemsError
        });
    }

    [HttpGet("Tracks/{id}")]
    [Authorize]
    public async Task<ActionResult> Track(string id, CancellationToken ct)
    {
        var track = await _spotify.GetTrackAsync(await ResolveLinkedUserIdAsync(ct).ConfigureAwait(false), id, ct).ConfigureAwait(false);
        if (track == null)
        {
            return NotFound();
        }

        return Ok(track);
    }

    [HttpGet("Albums/{id}")]
    [Authorize]
    public async Task<ActionResult> Album(string id, CancellationToken ct)
    {
        var tracks = await _spotify.GetAlbumTracksAsync(await ResolveLinkedUserIdAsync(ct).ConfigureAwait(false), id, ct).ConfigureAwait(false);
        if (tracks.Count == 0)
        {
            return NotFound();
        }

        var first = tracks[0];
        return Ok(new
        {
            Album = new
            {
                Id = id,
                Name = first.Album,
                ImageUrl = first.CoverUrl,
                Artists = first.AlbumArtist
            },
            Tracks = tracks
        });
    }

    [HttpGet("Library")]
    [Authorize]
    public async Task<ActionResult> LibraryHome(CancellationToken ct)
    {
        var currentUserId = GetUserId();
        var tokens = await _store.GetTokensAsync(currentUserId, ct).ConfigureAwait(false);
        if (tokens == null || tokens.RelinkRequired)
        {
            return Ok(new { Linked = false });
        }

        var userId = await ResolveLinkedUserIdAsync(ct).ConfigureAwait(false);
        var playlistsTask = _spotify.GetUserPlaylistsAsync(userId, ct, 40);
        var albumsTask = _spotify.GetSavedAlbumsAsync(userId, 40, ct);
        var artistsTask = _spotify.GetFollowedArtistsAsync(userId, 40, ct);
        var likedTask = _spotify.GetLikedPreviewAsync(userId, 8, ct);
        var recentTask = _spotify.GetRecentlyPlayedAsync(userId, 50, ct);
        var topTracksTask = _spotify.GetTopTracksAsync(userId, 50, "medium_term", ct);
        var topArtistsTask = _spotify.GetTopArtistsAsync(userId, 50, "medium_term", ct);
        await Task.WhenAll(playlistsTask, albumsTask, artistsTask, likedTask, recentTask, topTracksTask, topArtistsTask).ConfigureAwait(false);

        var playlists = playlistsTask.Result;
        var liked = likedTask.Result;
        var albums = albumsTask.Result;
        var artists = artistsTask.Result;
        var recent = recentTask.Result;
        var topTracks = topTracksTask.Result;
        var topArtists = topArtistsTask.Result;
        var scope = tokens.Scope ?? string.Empty;
        var preview = 8;
        return Ok(new
        {
            Linked = true,
            SpotifyDisplayName = tokens.DisplayName,
            SpotifyUserId = tokens.SpotifyUserId,
            NeedsRelink = scope.IndexOf("user-top-read", StringComparison.OrdinalIgnoreCase) < 0
                || scope.IndexOf("user-read-recently-played", StringComparison.OrdinalIgnoreCase) < 0,
            PlaylistCount = playlists.Count,
            Playlists = playlists.Take(preview).ToList(),
            Albums = albums.Take(preview).ToList(),
            AlbumCount = albums.Count,
            Artists = artists.Take(preview).ToList(),
            ArtistCount = artists.Count,
            LikedCount = liked.Total,
            LikedPreview = liked.Preview,
            RecentlyPlayed = recent.Take(preview).ToList(),
            RecentlyPlayedCount = recent.Count,
            TopTracks = topTracks.Take(preview).ToList(),
            TopTrackCount = topTracks.Count,
            TopArtists = topArtists.Take(preview).ToList(),
            TopArtistCount = topArtists.Count
        });
    }

    [HttpGet("Library/Albums")]
    [Authorize]
    public async Task<ActionResult> SavedAlbums(CancellationToken ct)
    {
        var albums = await _spotify.GetSavedAlbumsAsync(await ResolveLinkedUserIdAsync(ct).ConfigureAwait(false), 400, ct)
            .ConfigureAwait(false);
        return Ok(albums);
    }

    [HttpGet("Library/Artists")]
    [Authorize]
    public async Task<ActionResult> FollowedArtists(CancellationToken ct)
    {
        var artists = await _spotify.GetFollowedArtistsAsync(await ResolveLinkedUserIdAsync(ct).ConfigureAwait(false), 400, ct)
            .ConfigureAwait(false);
        return Ok(artists);
    }

    [HttpGet("Library/RecentlyPlayed")]
    [Authorize]
    public async Task<ActionResult> RecentlyPlayed(CancellationToken ct)
    {
        var tracks = await _spotify.GetRecentlyPlayedAsync(await ResolveLinkedUserIdAsync(ct).ConfigureAwait(false), 50, ct)
            .ConfigureAwait(false);
        return Ok(tracks);
    }

    [HttpGet("Library/TopTracks")]
    [Authorize]
    public async Task<ActionResult> TopTracks([FromQuery] string? range, CancellationToken ct)
    {
        var timeRange = range is "short_term" or "long_term" ? range : "medium_term";
        var tracks = await _spotify.GetTopTracksAsync(await ResolveLinkedUserIdAsync(ct).ConfigureAwait(false), 50, timeRange, ct)
            .ConfigureAwait(false);
        return Ok(tracks);
    }

    [HttpGet("Library/TopArtists")]
    [Authorize]
    public async Task<ActionResult> TopArtists([FromQuery] string? range, CancellationToken ct)
    {
        var timeRange = range is "short_term" or "long_term" ? range : "medium_term";
        var artists = await _spotify.GetTopArtistsAsync(await ResolveLinkedUserIdAsync(ct).ConfigureAwait(false), 50, timeRange, ct)
            .ConfigureAwait(false);
        return Ok(artists);
    }

    [HttpGet("Artists/{id}")]
    [Authorize]
    public async Task<ActionResult> Artist(string id, CancellationToken ct)
    {
        var userId = await ResolveLinkedUserIdAsync(ct).ConfigureAwait(false);
        var artist = await _spotify.GetArtistAsync(userId, id, ct).ConfigureAwait(false);
        if (artist == null)
        {
            return NotFound();
        }

        var albums = await _spotify.GetArtistAlbumsAsync(userId, id, ct).ConfigureAwait(false);
        var topTracks = await _spotify.GetArtistTopTracksAsync(userId, id, ct).ConfigureAwait(false);
        return Ok(new { Artist = artist, Albums = albums, TopTracks = topTracks });
    }

    [HttpGet("Library/Exists")]
    [Authorize]
    public async Task<ActionResult> LibraryExists([FromQuery] string? ids, CancellationToken ct)
    {
        var map = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(ids))
        {
            return Ok(map);
        }

        foreach (var id in ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct())
        {
            map[id] = await _storage.TrackFileExistsAsync(id, ct).ConfigureAwait(false);
        }

        return Ok(map);
    }

    public class LibraryExistsTrack
    {
        public string Id { get; set; } = string.Empty;

        public string? Name { get; set; }

        public List<string>? Artists { get; set; }

        public string? Album { get; set; }

        public string? Isrc { get; set; }

        public int DurationMs { get; set; }
    }

    public class LibraryExistsRequest
    {
        public List<LibraryExistsTrack> Tracks { get; set; } = [];
    }

    [HttpPost("Library/Exists")]
    [Authorize]
    public async Task<ActionResult> LibraryExistsPost([FromBody] LibraryExistsRequest? request, CancellationToken ct)
    {
        var map = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var row in request?.Tracks ?? [])
        {
            if (string.IsNullOrWhiteSpace(row.Id))
            {
                continue;
            }

            var track = new SpotifyTrackInfo
            {
                Id = row.Id,
                Name = row.Name ?? string.Empty,
                Artists = row.Artists ?? [],
                Album = row.Album ?? string.Empty,
                Isrc = row.Isrc,
                DurationMs = row.DurationMs
            };
            map[row.Id] = await _storage.IsOwnedAsync(track, ct).ConfigureAwait(false);
        }

        return Ok(map);
    }

    [HttpPost("Playlists/{id}/Monitor")]
    [Authorize]
    public async Task<ActionResult> MonitorPlaylist(string id, CancellationToken ct)
    {
        var userId = await ResolveLinkedUserIdAsync(ct).ConfigureAwait(false);
        var settings = await _store.GetUserSettingsAsync(userId, ct).ConfigureAwait(false);
        if (!settings.MonitoredPlaylistIds.Contains(id))
        {
            settings.MonitoredPlaylistIds.Add(id);
        }

        settings.Enabled = true;
        await _store.SaveUserSettingsAsync(settings, ct).ConfigureAwait(false);
        return Ok(new { Monitored = true, PlaylistId = id });
    }

    [HttpPost("Albums/{id}/Monitor")]
    [Authorize]
    public async Task<ActionResult> MonitorAlbum(string id, CancellationToken ct)
    {
        var userId = await ResolveLinkedUserIdAsync(ct).ConfigureAwait(false);
        var settings = await _store.GetUserSettingsAsync(userId, ct).ConfigureAwait(false);
        if (!settings.MonitoredAlbumIds.Contains(id))
        {
            settings.MonitoredAlbumIds.Add(id);
        }

        settings.Enabled = true;
        await _store.SaveUserSettingsAsync(settings, ct).ConfigureAwait(false);
        return Ok(new { Monitored = true, AlbumId = id });
    }

    [HttpGet("LikedSongs")]
    [Authorize]
    public async Task<ActionResult> LikedSongs(CancellationToken ct)
    {
        var tracks = await _spotify.GetLikedSongsAsync(await ResolveLinkedUserIdAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        return Ok(tracks);
    }

    public class MonitorRequest
    {
        public List<string>? PlaylistIds { get; set; }

        public List<string>? AlbumIds { get; set; }

        public bool? SyncLikedSongs { get; set; }

        public bool? Enabled { get; set; }

        public List<string>? IncludeArtistFilters { get; set; }

        public List<string>? ExcludeArtistFilters { get; set; }
    }

    [HttpPost("Settings")]
    [Authorize]
    public async Task<ActionResult> UpdateSettings([FromBody] MonitorRequest request, CancellationToken ct)
    {
        var userId = await ResolveLinkedUserIdAsync(ct).ConfigureAwait(false);
        var settings = await _store.GetUserSettingsAsync(userId, ct).ConfigureAwait(false);
        if (request.PlaylistIds != null)
        {
            settings.MonitoredPlaylistIds = request.PlaylistIds;
        }

        if (request.AlbumIds != null)
        {
            settings.MonitoredAlbumIds = request.AlbumIds;
        }

        if (request.SyncLikedSongs.HasValue)
        {
            settings.SyncLikedSongs = request.SyncLikedSongs.Value;
        }

        if (request.Enabled.HasValue)
        {
            settings.Enabled = request.Enabled.Value;
        }

        if (request.IncludeArtistFilters != null)
        {
            settings.IncludeArtistFilters = request.IncludeArtistFilters;
        }

        if (request.ExcludeArtistFilters != null)
        {
            settings.ExcludeArtistFilters = request.ExcludeArtistFilters;
        }

        await _store.SaveUserSettingsAsync(settings, ct).ConfigureAwait(false);
        return Ok(settings);
    }

    public class QueueTracksRequest
    {
        public List<string> TrackIds { get; set; } = [];

        public string? PlaylistId { get; set; }

        public string? PlaylistName { get; set; }
    }

    [HttpPost("Queue/Tracks")]
    [Authorize]
    public Task<ActionResult> QueueTracks([FromBody] QueueTracksRequest request, CancellationToken ct)
        => StartBrowserOperation(new() { Kind="tracks", TrackIds=request.TrackIds, SourceId=request.PlaylistId, SourceName=request.PlaylistName },ct);

    [HttpPost("Queue/Playlist/{id}")]
    [Authorize]
    public Task<ActionResult> QueuePlaylist(string id,CancellationToken ct)
        => StartBrowserOperation(new() { Kind="playlist",SourceId=id },ct);

    [HttpPost("Queue/Liked")]
    [Authorize]
    public Task<ActionResult> QueueLiked(CancellationToken ct)
        => StartBrowserOperation(new() { Kind="liked" },ct);

    [HttpPost("Queue/Album/{id}")]
    [Authorize]
    public Task<ActionResult> QueueAlbum(string id,CancellationToken ct)
        => StartBrowserOperation(new() { Kind="album",SourceId=id },ct);

    [HttpGet("Queue")]
    [Authorize]
    public async Task<ActionResult> GetQueue([FromQuery] string? status, CancellationToken ct)
    {
        var items = await _store.GetUserQueueAsync(GetUserId(), status, ct).ConfigureAwait(false);
        return Ok(items.Select(item => new { item.Id, item.SpotifyTrackId, item.Title, item.Artists, item.Album, item.Status, item.MatchScore, item.PlaylistName, item.Error, item.UpdatedAtUtc }));
    }

    [HttpPost("Queue/{id}/Rematch")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult> Rematch(string id, CancellationToken ct)
    {
        if (!await _store.RetryJobAsync(id, null, true, ct)) return NotFound();
        _queue.EnsureWorker();
        return NoContent();
    }

    [HttpPost("Queue/{id}/Retry")]
    [Authorize]
    public async Task<ActionResult> Retry(string id, CancellationToken ct)
    {
        if (!await _store.RetryJobAsync(id, GetUserId(), ct: ct)) return NotFound();
        _queue.EnsureWorker();
        return NoContent();
    }

    [HttpPost("Queue/RetryFailed")]
    [Authorize]
    public async Task<ActionResult> RetryFailed(CancellationToken ct)
    {
        var failed = await _store.GetUserQueueAsync(GetUserId(), "Failed", ct);
        var count = 0;
        foreach (var item in failed)
            if (await _store.RetryJobAsync(item.Id, GetUserId(), ct: ct)) count++;
        _queue.EnsureWorker();
        return Ok(new { Retried = count });
    }

    [HttpPost("Sync/Now")]
    [Authorize]
    public Task<ActionResult> SyncNow(CancellationToken ct)
        => StartBrowserOperation(new() { Kind="sync" },ct);

    private static void Tally(EnqueueResult result, ref int queued, ref int skipped)
    {
        if (result is EnqueueResult.Added or EnqueueResult.Retried)
        {
            queued++;
            return;
        }

        skipped++;
    }

    private static string NormalizeAudioQuality(string? value)
    {
        var quality = (value ?? "highest").Trim().ToLowerInvariant();
        return quality is "highest" or "high" or "medium" or "low" ? quality : "highest";
    }

    private Guid GetUserId()
    {
        var ids = User.Claims.Where(c => c.Type.Equals("Jellyfin-UserId", StringComparison.OrdinalIgnoreCase))
            .Select(c => Guid.TryParse(c.Value, out var id) ? id : Guid.Empty).Distinct().ToList();
        if (ids.Count != 1 || ids[0] == Guid.Empty || _userManager.GetUserById(ids[0]) == null)
            throw new UnauthorizedAccessException("Unable to resolve authenticated Jellyfin user.");
        return ids[0];
    }

    private async Task<Guid> ResolveLinkedUserIdAsync(CancellationToken ct)
    {
        var id = GetUserId();
        if (await _auth.GetValidTokensAsync(id, ct) == null)
            throw new InvalidOperationException("Link your Spotify account from Sync.");
        return id;
    }

    private ActionResult ServeBrowserResources(string contentType,params string[] names)
    {
        var resources=new List<string>();
        foreach(var name in names)
        {
            using var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream($"{typeof(Plugin).Namespace}.{name}");
            if(stream==null)return NotFound();
            using var reader=new StreamReader(stream);resources.Add(reader.ReadToEnd());
        }
        return Content(string.Join("\n",resources),contentType);
    }

    private ActionResult ServeEmbedded(string resourceName, string contentType)
    {
        Stream? stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream($"{typeof(Plugin).Namespace}.{resourceName}");
        if (stream == null)
        {
            return NotFound();
        }

        return File(stream, contentType);
    }
}
