using Jellyfin.Plugin.JellySpot.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellySpot.Controllers;

public partial class JellySpotController
{
    private ActionResult BrowserResponse<T>(T value)
    {
        var revision = System.Text.Json.JsonSerializer.SerializeToElement(value).TryGetProperty("Revision", out var pageRevision) ? pageRevision.GetString()! : Services.Storage.JellySpotStore.Revision(value!);
        var tag = "\"" + revision + "\""; Response.Headers.ETag = tag; Response.Headers.CacheControl = "private, no-cache";
        return Request.Headers.IfNoneMatch == tag ? StatusCode(304) : Ok(value);
    }
    [HttpPost("Operations")]
    [Authorize]
    public async Task<ActionResult> StartBrowserOperation([FromBody] OperationInput input, CancellationToken ct)
    {
        if (input.Kind is not ("tracks" or "playlist" or "album" or "liked" or "sync" or "refresh" or "inspect-playlist" or "inspect-album")) return BadRequest(new { Error = "Unknown operation kind." });
        if (input.TrackIds.Count > 10000 || input.IdempotencyKey?.Length > 200) return BadRequest();
        if (input.Kind is "playlist" or "album" or "inspect-playlist" or "inspect-album" && string.IsNullOrWhiteSpace(input.SourceId)) return BadRequest();
        if (input.Kind == "tracks" && input.TrackIds.Count == 0 && !input.SelectAll) return BadRequest();
        if (input.SelectAll && input.CollectionId == null && string.IsNullOrWhiteSpace(input.CatalogKind)) return BadRequest();
        if (input.CatalogKind != null && !(input.CatalogKind is "liked" or "recent" or "top-tracks" || input.CatalogKind.StartsWith("playlist:", StringComparison.Ordinal) || input.CatalogKind.StartsWith("album:", StringComparison.Ordinal))) return BadRequest();
        var userId = GetUserId();
        // Persist acknowledgement without performing a Spotify HTTP request.
        var tokens = await _store.GetTokensAsync(userId, ct);
        if (tokens == null || tokens.RelinkRequired) return Conflict(new { Error = "Link your Spotify account in Account first." });
        if (input.CollectionId != null)
        {
            var collection = await _store.CollectionMetadataAsync(input.CollectionId, userId, ct);
            if (collection == null || collection.OwnerId != userId) return NotFound();
        }
        var operation = await _store.AcceptOperationAsync(userId, tokens.Generation, input, ct);
        return Accepted($"JellySpot/Operations/{operation.Id}", new { operation.Id, operation.Status, StatusUrl = $"JellySpot/Operations/{operation.Id}" });
    }
    [HttpGet("Operations")]
    [Authorize]
    public async Task<ActionResult> BrowserOperations([FromQuery] PageQuery query, CancellationToken ct)
        => BrowserResponse(await _store.OperationPageAsync(GetUserId(), query, ct));

    [HttpGet("Operations/{id}")]
    [Authorize]
    public async Task<ActionResult> BrowserOperation(string id, CancellationToken ct)
    {
        var operation = await _store.OperationAsync(id, GetUserId(), ct); return operation == null ? NotFound() : BrowserResponse(operation);
    }
    [HttpPost("Operations/{id}/Cancel")]
    [Authorize]
    public async Task<ActionResult> CancelBrowserOperation(string id, CancellationToken ct)
        => await _store.CancelOperationAsync(id, GetUserId(), ct) ? NoContent() : NotFound();

    [HttpGet("Requests")]
    [Authorize]
    public async Task<ActionResult> BrowserRequests([FromQuery] PageQuery query, CancellationToken ct)
        => BrowserResponse(await _store.RequestPageAsync(GetUserId(), query, ct));
    [HttpPost("Requests/Actions")]
    [Authorize]
    public async Task<ActionResult> BrowserRequestAction([FromBody] RequestAction action, CancellationToken ct)
    {
        if (action.Action is not ("pause" or "resume" or "cancel" or "retry" or "priority" or "clear") || action.Ids.Count > 1000) return BadRequest();
        var changed = await _store.ChangeRequestsAsync(GetUserId(), action, ct); _queue.EnsureWorker(); return Ok(new { Changed = changed });
    }
    [HttpGet("Activity")]
    [Authorize]
    public async Task<ActionResult> BrowserActivity(CancellationToken ct)
    {
        var userId = GetUserId(); var settings = await _store.GetUserSettingsAsync(userId, ct);
        var counts = await _store.RequestCountsAsync(userId, ct);
        var paused = await _store.DownloadsPausedAsync(userId, ct);
        var operations = (await _store.OperationPageAsync(userId, new() { PageSize = 20, Sort = "activity" }, ct)).Items;
        var snapshot = new { Counts = counts, Paused = paused, Operations = operations, settings.LastSyncUtc, settings.LastSuccessfulSyncUtc, settings.LastSyncStatus, settings.LastError, Schedule = "Managed by Jellyfin scheduled tasks", DownloadNotice = "Sync prepares requests. Downloads and library scans finish separately." };
        var node = System.Text.Json.JsonSerializer.SerializeToNode(snapshot)!.AsObject();
        node["Revision"] = Services.Storage.JellySpotStore.Revision(snapshot);
        return BrowserResponse(node);
    }
    [HttpGet("Browser/Collections")]
    [Authorize]
    public async Task<ActionResult> BrowserCollections([FromQuery] string view, [FromQuery] PageQuery query, CancellationToken ct)
    {
        if (view is not ("mine" or "shared" or "following")) return BadRequest();
        return BrowserResponse(await _store.CollectionPageAsync(GetUserId(), view, query, id => _userManager.GetUserById(id)?.Username, ct));
    }
    [HttpGet("Browser/Collections/{id}")]
    [Authorize]
    public async Task<ActionResult> BrowserCollectionMetadata(string id, CancellationToken ct)
    {
        var userId = GetUserId(); var collection = await _store.CollectionMetadataAsync(id, userId, ct);
        if (collection == null || _userManager.GetUserById(collection.OwnerId) == null) return NotFound();
        var settings = await _store.GetUserSettingsAsync(userId, ct);
        return BrowserResponse(new { collection.Id, collection.Name, collection.CoverUrl, collection.SourceId, collection.Shared, collection.UpdatedAtUtc, Owned = collection.OwnerId == userId, OwnerName = _userManager.GetUserById(collection.OwnerId)!.Username, Following = await _store.IsFollowingAsync(id, userId, ct), Monitored = collection.OwnerId == userId && (collection.SourceId == "liked" ? settings.SyncLikedSongs : settings.MonitoredPlaylistIds.Contains(collection.SourceId)) });
    }
    [HttpGet("Browser/Collections/{id}/Tracks")]
    [Authorize]
    public async Task<ActionResult> BrowserCollectionTracks(string id, [FromQuery] PageQuery query, CancellationToken ct)
    {
        var userId = GetUserId(); var collection = await _store.CollectionMetadataAsync(id, userId, ct);
        if (collection == null || _userManager.GetUserById(collection.OwnerId) == null) return NotFound();
        var available = await Availability.ResolveAsync(userId, id, collection.UpdatedAtUtc?.ToString("O") ?? "legacy", ct);
        return BrowserResponse(await _store.TrackPageAsync(id, query, available, ct));
    }
    [HttpPost("Browser/Collections/{id}/Monitoring")]
    [Authorize]
    public async Task<ActionResult> BrowserMonitoring(string id, [FromBody] MonitoringInput input, CancellationToken ct)
    {
        var userId = GetUserId(); var collection = await _store.CollectionMetadataAsync(id, userId, ct);
        if (collection?.OwnerId != userId) return NotFound();
        return await SetMonitoringAsync(userId, collection.SourceId, "playlist", input.Enabled, ct);
    }
    public sealed class MonitoringInput { public bool Enabled { get; set; } }
    private async Task<ActionResult> SetMonitoringAsync(Guid userId, string source, string kind, bool enabled, CancellationToken ct)
    {
        var settings = await _store.GetUserSettingsAsync(userId, ct);
        if (source == "liked") settings.SyncLikedSongs = enabled;
        else
        {
            var ids = kind == "album" ? settings.MonitoredAlbumIds : settings.MonitoredPlaylistIds;
            ids.RemoveAll(id => id == source); if (enabled) ids.Add(source);
        }
        await _store.SaveUserSettingsAsync(settings, ct); return NoContent();
    }
    [HttpPost("Browser/Spotify/{kind}/{id}/Monitoring")]
    [Authorize]
    public Task<ActionResult> BrowserSpotifyMonitoring(string kind, string id, [FromBody] MonitoringInput input, CancellationToken ct)
        => kind is "playlist" or "album" or "liked" ? SetMonitoringAsync(GetUserId(), id, kind, input.Enabled, ct) : Task.FromResult<ActionResult>(BadRequest());

    [HttpGet("Browser/Spotify/{kind}")]
    [Authorize]
    public async Task<ActionResult> BrowserSpotifyCatalog(string kind, [FromQuery] PageQuery query, CancellationToken ct)
    {
        if (kind is not ("playlists" or "albums" or "artists" or "liked" or "recent" or "top-tracks" or "top-artists")) return BadRequest();
        var userId = GetUserId(); var tokens = await _store.GetTokensAsync(userId, ct); if (tokens == null || tokens.RelinkRequired) return Conflict();
        return BrowserResponse(await _store.CatalogPageAsync(userId, tokens.Generation, kind, query, ct));
    }
    [HttpGet("Browser/Spotify/{kind}/{id}/Tracks")]
    [Authorize]
    public async Task<ActionResult> BrowserSpotifyTracks(string kind, string id, [FromQuery] PageQuery query, CancellationToken ct)
    {
        if (kind is not ("playlist" or "album" or "liked" or "recent" or "top-tracks")) return BadRequest();
        var userId = GetUserId(); var tokens = await _store.GetTokensAsync(userId, ct); if (tokens == null || tokens.RelinkRequired) return Conflict();
        var catalogKind = kind is "playlist" or "album" ? $"{kind}:{id}" : kind;
        var scope = Services.Storage.JellySpotStore.CatalogScope(userId, tokens.Generation, catalogKind);
        var catalog = await _store.CatalogPageAsync(userId, tokens.Generation, catalogKind, new() { PageSize = 1 }, ct);
        var available = await Availability.ResolveAsync(userId, scope, catalog.Revision, ct);
        return BrowserResponse((await _store.TrackPageAsync(scope, query, available, ct)) with { Complete = catalog.Complete });
    }
}
