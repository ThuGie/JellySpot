using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellySpot.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellySpot.Controllers;

public partial class JellySpotController
{
    [HttpGet("admin/Queue")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult> AdminQueue([FromQuery] string? status, CancellationToken ct)
    {
        var items = await _store.GetQueueAsync(status, 300, ct);
        return Ok(items.Select(item => new { item.Id, item.SpotifyTrackId, item.Title, item.Artists,
            item.Album, item.Status, item.MatchScore, item.YoutubeVideoId, item.Error, item.UpdatedAtUtc }));
    }

    [HttpPost("admin/Queue/{id}/Retry")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult> AdminRetry(string id, CancellationToken ct)
    {
        if (!await _store.RetryJobAsync(id, null, ct: ct)) return NotFound();
        _queue.EnsureWorker();
        return NoContent();
    }

    [HttpPost("admin/Queue/{id}/Rematch")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult> AdminRematch(string id, CancellationToken ct)
    {
        if (!await _store.RetryJobAsync(id, null, true, ct)) return NotFound();
        _queue.EnsureWorker();
        return NoContent();
    }

    [HttpPost("admin/Queue/RetryFailed")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult> AdminRetryFailed(CancellationToken ct)
    {
        var items = await _store.GetQueueAsync("Failed", 10000, ct);
        foreach (var item in items) await _store.RetryJobAsync(item.Id, null, ct: ct);
        _queue.EnsureWorker();
        return Ok(new { Retried = items.Count });
    }

    [HttpGet("Collections")]
    [Authorize]
    public async Task<ActionResult> Collections([FromQuery] string view = "mine", CancellationToken ct = default)
    {
        if (view is not ("mine" or "shared" or "following")) return BadRequest();
        var userId = GetUserId();
        await _store.RemoveMissingOwnersAsync(id => _userManager.GetUserById(id) != null, ct);
        var collections = await _store.GetCollectionsAsync(userId, view, ct);
        var result = new List<object>();
        foreach (var collection in collections)
        {
            var owner = _userManager.GetUserById(collection.OwnerId);
            if (owner == null) continue;
            result.Add(new { collection.Id, collection.Name, collection.CoverUrl, collection.Shared,
                collection.UpdatedAtUtc, TrackCount = collection.Tracks.Count, Owned = collection.OwnerId == userId,
                OwnerName = owner.Username, Following = await _store.IsFollowingAsync(collection.Id, userId, ct) });
        }
        return Ok(result);
    }

    [HttpGet("Collections/{id}")]
    [Authorize]
    public async Task<ActionResult> Collection(string id, CancellationToken ct)
    {
        var userId = GetUserId();
        var collection = await _store.GetCollectionAsync(id, userId, ct);
        if (collection == null || _userManager.GetUserById(collection.OwnerId) == null) return NotFound();
        var user = _userManager.GetUserById(userId)!;
        // Query through Jellyfin's user-aware library filtering; never stream raw paths.
        var audio = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.Audio], Recursive = true, IsVirtualItem = false
        }).OfType<Audio>().ToList();
        var rows = new List<object>();
        foreach (var track in collection.Tracks)
        {
            ct.ThrowIfCancellationRequested();
            var index = await _store.GetTrackIndexAsync(track.Id, ct);
            var item = audio.FirstOrDefault(a => !string.IsNullOrEmpty(index?.RelativePath)
                && string.Equals(a.Path, _storage.GetAbsolutePath(index.RelativePath), StringComparison.OrdinalIgnoreCase));
            item ??= Services.Storage.LibraryMatchService.FindVisibleAudio(track, audio);
            rows.Add(new { track.Id, track.Name, track.Artists, track.Album, track.DurationMs,
                ItemId = item?.Id.ToString("N"), Available = item != null });
        }
        return Ok(new { collection.Id, collection.Name, collection.CoverUrl, collection.Shared,
            collection.UpdatedAtUtc, Owned = collection.OwnerId == userId,
            OwnerName = _userManager.GetUserById(collection.OwnerId)!.Username,
            Following = await _store.IsFollowingAsync(id, userId, ct), Tracks = rows });
    }

    public class SharingRequest { public bool Shared { get; set; } }

    [HttpPost("Collections/{id}/Sharing")]
    [Authorize]
    public async Task<ActionResult> ShareCollection(string id, [FromBody] SharingRequest request, CancellationToken ct)
        => await _store.SetSharingAsync(id, GetUserId(), request.Shared, ct) ? NoContent() : NotFound();

    [HttpPost("Collections/{id}/Follow")]
    [Authorize]
    public async Task<ActionResult> FollowCollection(string id, CancellationToken ct)
    {
        var collection = await _store.GetCollectionAsync(id, GetUserId(), ct);
        if (collection == null || _userManager.GetUserById(collection.OwnerId) == null) return NotFound();
        return await _store.FollowCollectionAsync(id, GetUserId(), true, ct) ? NoContent() : NotFound();
    }

    [HttpDelete("Collections/{id}/Follow")]
    [Authorize]
    public async Task<ActionResult> UnfollowCollection(string id, CancellationToken ct)
    {
        await _store.FollowCollectionAsync(id, GetUserId(), false, ct);
        return NoContent();
    }

    [HttpDelete("Collections/{id}")]
    [Authorize]
    public async Task<ActionResult> DeleteCollection(string id, CancellationToken ct)
    {
        var userId = GetUserId();
        var collection = await _store.GetCollectionAsync(id, userId, ct);
        if (collection?.OwnerId != userId) return NotFound();
        var settings = await _store.GetUserSettingsAsync(userId, ct);
        if (collection.SourceId == "liked") settings.SyncLikedSongs = false;
        else settings.MonitoredPlaylistIds.Remove(collection.SourceId);
        await _store.SaveUserSettingsAsync(settings, ct);
        return await _store.DeleteCollectionAsync(id, userId, ct) ? NoContent() : NotFound();
    }
}
