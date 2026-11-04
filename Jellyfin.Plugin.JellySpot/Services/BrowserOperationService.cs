using Jellyfin.Plugin.JellySpot.Models;
using Jellyfin.Plugin.JellySpot.Services.Download;
using Jellyfin.Plugin.JellySpot.Services.Spotify;
using Jellyfin.Plugin.JellySpot.Services.Storage;
using Jellyfin.Plugin.JellySpot.Services.Sync;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellySpot.Services;

public sealed class BrowserOperationService : BackgroundService
{
    private readonly JellySpotStore _store;
    private readonly SpotifyAuthService _auth;
    private readonly SpotifyApiClient _spotify;
    private readonly BrowserAvailabilityService _availability;
    private readonly SyncEngine _sync;
    private readonly DownloadQueueService _queue;
    private readonly IUserManager _users;
    private readonly ILogger<BrowserOperationService> _logger;
    public BrowserOperationService(JellySpotStore store, SpotifyAuthService auth, SpotifyApiClient spotify, SyncEngine sync, DownloadQueueService queue, IUserManager users, ILogger<BrowserOperationService> logger, BrowserAvailabilityService availability)
    { _availability = availability; _store = store; _auth = auth; _spotify = spotify; _sync = sync; _queue = queue; _users = users; _logger = logger; }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var running = new Dictionary<Guid, Task>();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                foreach (var key in running.Where(x => x.Value.IsCompleted).Select(x => x.Key).ToArray()) running.Remove(key);
                try
                {
                    foreach (var operation in await _store.OperationsAsync(null, true, stoppingToken))
                    {
                        if (running.Count >= 2) break;
                        if (!running.ContainsKey(operation.UserId)) running[operation.UserId] = RunAsync(operation, stoppingToken);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { _logger.LogError(ex, "Could not read persisted operations; will retry"); }
                await Task.Delay(250, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { await Task.WhenAll(running.Values); }
    }
    public async Task RunAsync(BrowserOperation operation, CancellationToken ct = default)
    {
        async Task Report()
        {
            ct.ThrowIfCancellationRequested();
            if ((await _store.OperationAsync(operation.Id, null, ct))?.CancelRequested == true) throw new OperationCanceledException("Operation cancelled.");
            if (_users.GetUserById(operation.UserId) == null) throw new UnauthorizedAccessException();
            await _auth.EnsureGenerationAsync(operation.UserId, operation.Generation, ct);
            await _store.SaveOperationAsync(operation, ct);
        }
        try
        {
            await Report(); operation.Status = "Running"; operation.StartedAtUtc ??= DateTime.UtcNow; operation.Processed = operation.Discovered = operation.Requested = operation.Existing = operation.Failed = 0;
            operation.Issues.Clear(); operation.Stage = "Reading Spotify"; await Report();
            if (operation.Input.Kind == "sync") await _sync.SyncUserAsync(operation.UserId, ct, true, operation, Report);
            else if (operation.Input.Kind == "refresh") await RefreshAsync(operation, Report, ct);
            else
            {
                var source = operation.Input.SourceId ?? ""; var name = operation.Input.SourceName ?? "Selection"; List<SpotifyTrackInfo> tracks;
                if (operation.Input.CollectionId != null)
                {
                    var collection = await _store.CollectionMetadataAsync(operation.Input.CollectionId, operation.UserId, ct);
                    if (collection == null || collection.OwnerId != operation.UserId) throw new UnauthorizedAccessException();
                    var entries = await _store.ScopeTracksAsync(collection.Id, ct);
                    if (operation.Input.SelectAll)
                    {
                        var query = (operation.Input.Query ?? "").Trim();
                        tracks = entries.Where(e => query.Length == 0 || ($"{e.Track.Name} {string.Join(' ', e.Track.Artists)} {e.Track.Album}").Contains(query, StringComparison.OrdinalIgnoreCase)).Select(e => e.Track).ToList();
                    }
                    else tracks = entries.Where(e => operation.Input.TrackIds.Contains(e.Track.Id)).Select(e => e.Track).ToList();
                    if (operation.Input.Filter is "missing" or "available")
                    {
                        var available = await _availability.ResolveAsync(operation.UserId, collection.Id, collection.UpdatedAtUtc?.ToString("O") ?? "legacy", ct);
                        tracks = tracks.Where(t => available.ContainsKey(t.Id) == (operation.Input.Filter == "available")).ToList();
                    }
                    source = collection.SourceId; name = collection.Name;
                }
                else if (operation.Input.SelectAll && operation.Input.CatalogKind != null)
                {
                    var kind = operation.Input.CatalogKind;
                    var scope = JellySpotStore.CatalogScope(operation.UserId, operation.Generation, kind);
                    var indexed = await _store.CatalogPageAsync(operation.UserId, operation.Generation, kind, new() { PageSize = 1 }, ct);
                    if (!indexed.Complete) throw new InvalidOperationException("Refresh this collection before selecting all matching tracks.");
                    var entries = await _store.ScopeTracksAsync(scope, ct);
                    if (kind == "liked") await _store.SaveCollectionAsync(new() { OwnerId = operation.UserId, SourceId = "liked", Name = "Liked Songs", UpdatedAtUtc = DateTime.UtcNow, Tracks = entries.Select(e => e.Track).ToList() }, ct);
                    var query = (operation.Input.Query ?? "").Trim();
                    tracks = entries.Where(e => query.Length == 0 || ($"{e.Track.Name} {string.Join(' ', e.Track.Artists)} {e.Track.Album}").Contains(query, StringComparison.OrdinalIgnoreCase)).Select(e => e.Track).ToList();
                    if (operation.Input.Filter is "missing" or "available")
                    {
                        var snapshot = await _store.CatalogPageAsync(operation.UserId, operation.Generation, kind, new() { PageSize = 1 }, ct);
                        var available = await _availability.ResolveAsync(operation.UserId, scope, snapshot.Revision, ct);
                        tracks = tracks.Where(t => available.ContainsKey(t.Id) == (operation.Input.Filter == "available")).ToList();
                    }
                    source = kind; name = operation.Input.SourceName ?? kind;
                }
                else if (operation.Input.Kind is "playlist" or "inspect-playlist")
                {
                    var result = await _spotify.GetPlaylistWithTracksAsync(operation.UserId, source, ct) ?? throw new HttpRequestException("Playlist unavailable.");
                    if (result.Playlist.ItemsRestricted) throw new HttpRequestException("Playlist membership unavailable.");
                    tracks = result.Tracks.ToList(); name = result.Playlist.Name;
                    await Report(); await _store.SaveCollectionAsync(new() { OwnerId = operation.UserId, SourceId = source, Name = name, CoverUrl = result.Playlist.ImageUrl, UpdatedAtUtc = DateTime.UtcNow, Tracks = tracks }, ct);
                }
                else if (operation.Input.Kind == "liked")
                {
                    source = "liked"; name = "Liked Songs"; tracks = (await _spotify.GetLikedSongsAsync(operation.UserId, ct)).ToList();
                    await Report(); await _store.SaveCollectionAsync(new() { OwnerId = operation.UserId, SourceId = source, Name = name, UpdatedAtUtc = DateTime.UtcNow, Tracks = tracks }, ct);
                }
                else if (operation.Input.Kind is "album" or "inspect-album") { tracks = (await _spotify.GetAlbumTracksAsync(operation.UserId, source, ct)).ToList(); name = tracks.FirstOrDefault()?.Album ?? "Album"; }
                else
                {
                    tracks = [];
                    foreach (var id in operation.Input.TrackIds.Distinct())
                    {
                        await Report();
                        var track = await _store.KnownTrackAsync(operation.UserId, operation.Generation, id, ct) ?? await _spotify.GetTrackAsync(operation.UserId, id, ct);
                        if (track != null) tracks.Add(track); else operation.Failed++;
                    }
                }
                if (operation.Input.Kind.StartsWith("inspect-", StringComparison.Ordinal))
                {
                    var kind = operation.Input.Kind[8..] + ":" + source;
                    await Report(); await _store.SaveCatalogAsync(operation.UserId, operation.Generation, kind, tracks.Cast<object>(), tracks, ct);
                    operation.CurrentCollection = name; operation.Discovered = operation.Processed = tracks.Count; operation.TotalKnown = true;
                    operation.Status = "Completed"; operation.Stage = "Metadata ready"; return;
                }
                tracks = tracks.DistinctBy(t => t.Id).ToList(); operation.Discovered = tracks.Count; operation.TotalKnown = true; operation.CurrentCollection = name; await Report();
                foreach (var track in tracks)
                {
                    if (operation.Processed % 20 == 0) await Report(); operation.Stage = "Checking local availability";
                    var result = await _queue.EnqueueTrackAsync(operation.UserId, track, source, name, ct, operation.Id);
                    operation.Processed++; if (result == EnqueueResult.AlreadyDownloaded) operation.Existing++; else operation.Requested++;
                    operation.Stage = "Creating download requests";
                }
            }
            await Report(); operation.Status = operation.Failed > 0 ? "PartiallyFailed" : "Completed"; operation.Stage = operation.Input.Kind == "sync" ? "Sync finished; download requests continue separately" : "Request preparation finished";
        }
        catch (OperationCanceledException)
        { operation.Status = ct.IsCancellationRequested ? "Queued" : "Cancelled"; operation.Stage = ct.IsCancellationRequested ? "Waiting to resume after restart" : "Cancelled; existing download requests were kept"; }
        catch (Exception ex)
        { operation.Failed++; operation.Status = "Failed"; operation.Stage = "Needs attention"; operation.Error = "The operation could not finish. Check Spotify connection and try again."; _logger.LogWarning(ex, "JellySpot operation {Operation} failed", operation.Id); }
        finally
        {
            if (operation.Status != "Queued") operation.FinishedAtUtc = DateTime.UtcNow;
            await _store.SaveOperationAsync(operation, CancellationToken.None);
        }
    }
    private async Task RefreshAsync(BrowserOperation operation, Func<Task> report, CancellationToken ct)
    {
        // Publish each independently successful shelf. Failed shelves retain their last good snapshot.
        async Task Shelf(string kind, Func<Task<IEnumerable<object>>> load, Func<IEnumerable<object>, IEnumerable<SpotifyTrackInfo>?>? trackRows = null)
        {
            operation.CurrentCollection = kind; operation.Stage = "Reading Spotify"; await report();
            try
            {
                var values = (await load()).ToList(); await report();
                await _store.SaveCatalogAsync(operation.UserId, operation.Generation, kind, values, trackRows?.Invoke(values), ct);
                operation.Discovered += values.Count; operation.Processed += values.Count;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { operation.Failed++; operation.Issues.Add(new(kind, kind, "Spotify source could not be refreshed. Last successful snapshot was kept.")); _logger.LogWarning(ex, "Metadata shelf {Kind} failed", kind); }
            await report();
        }
        var user = operation.UserId;
        await Shelf("playlists", async () => (await _spotify.GetUserPlaylistsAsync(user, ct)).Cast<object>());
        await Shelf("albums", async () => (await _spotify.GetSavedAlbumsAsync(user, int.MaxValue, ct)).Cast<object>());
        await Shelf("artists", async () => (await _spotify.GetFollowedArtistsAsync(user, int.MaxValue, ct)).Cast<object>());
        await Shelf("liked", async () => (await _spotify.GetLikedSongsAsync(user, ct)).Cast<object>(), values => values.Cast<SpotifyTrackInfo>());
        await Shelf("recent", async () => (await _spotify.GetRecentlyPlayedAsync(user, 50, ct)).Cast<object>(), values => values.Cast<SpotifyTrackInfo>());
        await Shelf("top-tracks", async () => (await _spotify.GetTopTracksAsync(user, 50, "medium_term", ct)).Cast<object>(), values => values.Cast<SpotifyTrackInfo>());
        await Shelf("top-artists", async () => (await _spotify.GetTopArtistsAsync(user, 50, "medium_term", ct)).Cast<object>());
        operation.TotalKnown = true;
    }
}
