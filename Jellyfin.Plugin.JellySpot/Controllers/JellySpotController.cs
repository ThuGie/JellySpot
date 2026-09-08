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
[Route("JellySpot")]
public class JellySpotController : ControllerBase
{
    private readonly SpotifyAuthService _auth;
    private readonly SpotifyApiClient _spotify;
    private readonly JellySpotStore _store;
    private readonly SyncEngine _sync;
    private readonly DownloadQueueService _queue;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IApplicationPaths _applicationPaths;
    private readonly FfmpegLocator _ffmpeg;
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
        ILogger<JellySpotController> logger)
    {
        _auth = auth;
        _spotify = spotify;
        _store = store;
        _sync = sync;
        _queue = queue;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _applicationPaths = applicationPaths;
        _ffmpeg = ffmpeg;
        _logger = logger;
    }

    [HttpGet("jellyspot-tabs.js")]
    public ActionResult GetTabsScript() => ServeEmbedded("Inject.jellyspot-tabs.js", "application/javascript");

    [HttpGet("jellyspot-tabs.css")]
    public ActionResult GetTabsStylesheet() => ServeEmbedded("Inject.jellyspot-tabs.css", "text/css");

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
        var userId = await ResolveLinkedUserIdAsync(ct).ConfigureAwait(false);
        var tokens = await _store.GetTokensAsync(userId, ct).ConfigureAwait(false);
        var settings = await _store.GetUserSettingsAsync(userId, ct).ConfigureAwait(false);
        return Ok(new
        {
            Linked = tokens != null,
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
            return Content($"<html><body><h2>Spotify authorization failed</h2><p>{error}</p></body></html>", "text/html");
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
            return Content($"<html><body><h2>Link failed</h2><p>{ex.Message}</p></body></html>", "text/html");
        }
    }

    [HttpPost("OAuth/Unlink")]
    [Authorize]
    public async Task<ActionResult> Unlink(CancellationToken ct)
    {
        foreach (var id in GetUserIdCandidates())
        {
            await _auth.UnlinkAsync(id, ct).ConfigureAwait(false);
        }
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

        return Ok(new { Playlist = result.Value.Playlist, Tracks = result.Value.Tracks });
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
    public async Task<ActionResult> QueueTracks([FromBody] QueueTracksRequest request, CancellationToken ct)
    {
        var userId = await ResolveLinkedUserIdAsync(ct).ConfigureAwait(false);
        var queued = 0;
        var skipped = 0;
        foreach (var trackId in request.TrackIds.Distinct())
        {
            var track = await _spotify.GetTrackAsync(userId, trackId, ct).ConfigureAwait(false);
            if (track == null)
            {
                continue;
            }

            Tally(await _queue.EnqueueTrackAsync(userId, track, request.PlaylistId, request.PlaylistName, ct).ConfigureAwait(false), ref queued, ref skipped);
        }

        return Ok(new { Queued = queued, Skipped = skipped });
    }

    [HttpPost("Queue/Playlist/{id}")]
    [Authorize]
    public async Task<ActionResult> QueuePlaylist(string id, CancellationToken ct)
    {
        var userId = await ResolveLinkedUserIdAsync(ct).ConfigureAwait(false);
        var result = await _spotify.GetPlaylistWithTracksAsync(userId, id, ct).ConfigureAwait(false);
        if (result == null)
        {
            return NotFound();
        }

        var queued = 0;
        var skipped = 0;
        foreach (var track in result.Value.Tracks)
        {
            Tally(await _queue.EnqueueTrackAsync(userId, track, result.Value.Playlist.Id, result.Value.Playlist.Name, ct).ConfigureAwait(false), ref queued, ref skipped);
        }

        var settings = await _store.GetUserSettingsAsync(userId, ct).ConfigureAwait(false);
        if (!settings.MonitoredPlaylistIds.Contains(id))
        {
            settings.MonitoredPlaylistIds.Add(id);
            await _store.SaveUserSettingsAsync(settings, ct).ConfigureAwait(false);
        }

        return Ok(new { Queued = queued, Skipped = skipped, Monitored = true });
    }

    [HttpPost("Queue/Liked")]
    [Authorize]
    public async Task<ActionResult> QueueLiked(CancellationToken ct)
    {
        var userId = await ResolveLinkedUserIdAsync(ct).ConfigureAwait(false);
        var tracks = await _spotify.GetLikedSongsAsync(userId, ct).ConfigureAwait(false);
        var queued = 0;
        var skipped = 0;
        foreach (var track in tracks)
        {
            Tally(await _queue.EnqueueTrackAsync(userId, track, "liked", "Liked Songs", ct).ConfigureAwait(false), ref queued, ref skipped);
        }

        var settings = await _store.GetUserSettingsAsync(userId, ct).ConfigureAwait(false);
        settings.SyncLikedSongs = true;
        await _store.SaveUserSettingsAsync(settings, ct).ConfigureAwait(false);
        return Ok(new { Queued = queued, Skipped = skipped });
    }

    [HttpPost("Queue/Album/{id}")]
    [Authorize]
    public async Task<ActionResult> QueueAlbum(string id, CancellationToken ct)
    {
        var userId = await ResolveLinkedUserIdAsync(ct).ConfigureAwait(false);
        var tracks = await _spotify.GetAlbumTracksAsync(userId, id, ct).ConfigureAwait(false);
        var queued = 0;
        var skipped = 0;
        foreach (var track in tracks)
        {
            Tally(await _queue.EnqueueTrackAsync(userId, track, id, track.Album, ct).ConfigureAwait(false), ref queued, ref skipped);
        }

        var settings = await _store.GetUserSettingsAsync(userId, ct).ConfigureAwait(false);
        if (!settings.MonitoredAlbumIds.Contains(id))
        {
            settings.MonitoredAlbumIds.Add(id);
            await _store.SaveUserSettingsAsync(settings, ct).ConfigureAwait(false);
        }

        return Ok(new { Queued = queued, Skipped = skipped });
    }

    [HttpGet("Queue")]
    [Authorize]
    public async Task<ActionResult> GetQueue([FromQuery] string? status, CancellationToken ct)
    {
        var items = await _store.GetQueueAsync(status, 300, ct).ConfigureAwait(false);
        return Ok(items);
    }

    [HttpPost("Queue/{id}/Rematch")]
    [Authorize]
    public async Task<ActionResult> Rematch(string id, CancellationToken ct)
    {
        await _queue.RematchAsync(id, ct).ConfigureAwait(false);
        return NoContent();
    }

    [HttpPost("Queue/{id}/Retry")]
    [Authorize]
    public async Task<ActionResult> Retry(string id, CancellationToken ct)
    {
        await _queue.RetryAsync(id, ct).ConfigureAwait(false);
        return NoContent();
    }

    [HttpPost("Queue/RetryFailed")]
    [Authorize]
    public async Task<ActionResult> RetryFailed(CancellationToken ct)
    {
        var count = await _queue.RetryFailedAsync(ct).ConfigureAwait(false);
        return Ok(new { Retried = count });
    }

    [HttpPost("Sync/Now")]
    [Authorize]
    public async Task<ActionResult> SyncNow(CancellationToken ct)
    {
        await _sync.SyncUserAsync(await ResolveLinkedUserIdAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        return Ok(new { Status = "started" });
    }

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
        var candidates = GetUserIdCandidates();
        if (candidates.Count > 0)
        {
            return candidates[0];
        }

        throw new UnauthorizedAccessException("Unable to resolve Jellyfin user.");
    }

    private List<Guid> GetUserIdCandidates()
    {
        var ids = new List<Guid>();

        void add(Guid id)
        {
            if (id != Guid.Empty && !ids.Contains(id))
            {
                ids.Add(id);
            }
        }

        // Same claim JellySeerr / Jellyfin 10.11 APIs use. Never take NameIdentifier first —
        // that GUID is often a session id, so tokens look "unlinked" on the next request.
        var jellyfinClaim = User.Claims.FirstOrDefault(c =>
            c.Type.Equals("Jellyfin-UserId", StringComparison.OrdinalIgnoreCase));
        if (jellyfinClaim != null && Guid.TryParse(jellyfinClaim.Value, out var jellyfinId))
        {
            add(jellyfinId);
        }

        foreach (var claim in User.Claims)
        {
            if ((claim.Type.Equals("UserId", StringComparison.OrdinalIgnoreCase) ||
                 claim.Type.Contains("Jellyfin-UserId", StringComparison.OrdinalIgnoreCase)) &&
                Guid.TryParse(claim.Value, out var id))
            {
                add(id);
            }
        }

        var name = User.Identity?.Name;
        if (!string.IsNullOrEmpty(name))
        {
            var user = _userManager.GetUserByName(name);
            if (user != null)
            {
                add(user.Id);
            }
        }

        return ids;
    }

    private async Task<Guid> ResolveLinkedUserIdAsync(CancellationToken ct)
    {
        var candidates = GetUserIdCandidates();
        if (candidates.Count == 0)
        {
            throw new UnauthorizedAccessException("Unable to resolve Jellyfin user.");
        }

        foreach (var id in candidates)
        {
            if (await _store.GetTokensAsync(id, ct).ConfigureAwait(false) != null)
            {
                return id;
            }
        }

        return candidates[0];
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
