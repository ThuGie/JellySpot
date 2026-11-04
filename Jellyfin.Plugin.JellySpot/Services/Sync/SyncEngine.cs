using Jellyfin.Plugin.JellySpot.Models;
using Jellyfin.Plugin.JellySpot.Services.Download;
using Jellyfin.Plugin.JellySpot.Services.Spotify;
using Jellyfin.Plugin.JellySpot.Services.Storage;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellySpot.Services.Sync;

public class SyncEngine
{
    private readonly IUserManager _users;
    private readonly SpotifyAuthService _auth;
    private readonly SpotifyApiClient _spotify;
    private readonly JellySpotStore _store;
    private readonly DownloadQueueService _queue;
    private readonly ILogger<SyncEngine> _logger;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, SemaphoreSlim> _gates = new();
    public SyncEngine(SpotifyApiClient spotify, IUserManager users, SpotifyAuthService auth, JellySpotStore store, DownloadQueueService queue, ILogger<SyncEngine> logger)
    { _spotify = spotify; _users = users; _auth = auth; _store = store; _queue = queue; _logger = logger; }

    public async Task SyncUserAsync(Guid userId, CancellationToken ct = default, bool manual = false, BrowserOperation? operation = null, Func<Task>? report = null)
    {
        var gate = _gates.GetOrAdd(userId, _ => new(1, 1)); await gate.WaitAsync(ct);
        try
        {
            var settings = await _store.GetUserSettingsAsync(userId, ct);
            if ((!manual && !settings.Enabled) || _users.GetUserById(userId) == null || await _auth.GetValidTokensAsync(userId, ct) == null) return;
            var completed = 0; var failed = 0; Exception? last = null;
            async Task Source(string source, string name, bool album = false)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (operation != null) { operation.CurrentCollection = name; operation.Stage = "Reading Spotify"; if (report != null) await report(); }
                    IReadOnlyList<SpotifyTrackInfo> tracks; string? cover = null;
                    if (source == "liked") tracks = await _spotify.GetLikedSongsAsync(userId, ct);
                    else if (album) tracks = await _spotify.GetAlbumTracksAsync(userId, source, ct);
                    else
                    {
                        var playlist = await _spotify.GetPlaylistWithTracksAsync(userId, source, ct) ?? throw new HttpRequestException("Spotify playlist unavailable.");
                        if (playlist.Playlist.ItemsRestricted) throw new HttpRequestException("Spotify playlist membership unavailable.");
                        tracks = playlist.Tracks; name = playlist.Playlist.Name; cover = playlist.Playlist.ImageUrl;
                    }
                    var filtered = FilterTracks(tracks, settings).ToList();
                    if (operation != null) { operation.Discovered += filtered.Count; operation.Stage = "Checking local availability"; if (report != null) await report(); }
                    var index = 0;
                    foreach (var track in filtered)
                    {
                        ct.ThrowIfCancellationRequested();
                        var result = await _queue.EnqueueTrackAsync(userId, track, source, name, ct, operation?.Id);
                        if (operation != null)
                        {
                            operation.Processed++;
                            if (result == EnqueueResult.AlreadyDownloaded) operation.Existing++; else operation.Requested++;
                            operation.Stage = "Creating download requests";
                            if (++index % 20 == 0 && report != null) await report();
                        }
                    }
                    if (!album) await _store.SaveCollectionAsync(new() { OwnerId = userId, SourceId = source, Name = name, CoverUrl = cover, UpdatedAtUtc = DateTime.UtcNow, Tracks = filtered }, ct);
                    completed++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { last = ex; failed++; if (operation != null) { operation.Failed++; operation.Issues.Add(new(source, name == "Playlist" ? "Playlist " + source : name, "Spotify source could not be read. Last successful membership was kept.")); } _logger.LogWarning(ex, "Could not sync collection {Source} for {User}", source, userId); }
                if (report != null) await report();
            }
            try
            {
                if (settings.SyncLikedSongs) await Source("liked", "Liked Songs");
                foreach (var id in settings.MonitoredPlaylistIds.Distinct()) await Source(id, "Playlist");
                foreach (var id in settings.MonitoredAlbumIds.Distinct()) await Source(id, "Album", true);
                if (failed > 0 && completed == 0) throw last!;
                var current = await _store.GetUserSettingsAsync(userId, ct); current.LastSyncUtc = DateTime.UtcNow; current.LastSyncStatus = failed > 0 ? "Partial" : "OK"; current.LastError = failed > 0 ? $"{failed} collections could not be synced." : null;
                if (failed == 0) current.LastSuccessfulSyncUtc = DateTime.UtcNow;
                await _store.SaveUserSettingsAsync(current, ct);
                if (operation != null) operation.TotalKnown = true;
                _queue.EnsureWorker();
            }
            catch (Exception ex)
            {
                var current = await _store.GetUserSettingsAsync(userId, CancellationToken.None); current.LastSyncUtc = DateTime.UtcNow; current.LastSyncStatus = ex is OperationCanceledException ? "Cancelled" : "Failed"; current.LastError = ex is OperationCanceledException ? null : "Sync could not finish. Check your Spotify connection and try again.";
                await _store.SaveUserSettingsAsync(current, CancellationToken.None); throw;
            }
        }
        finally { gate.Release(); }
    }
    public async Task SyncAllEnabledUsersAsync(CancellationToken ct = default)
    {
        foreach (var user in await _store.GetAllEnabledUsersAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            try { await SyncUserAsync(user.JellyfinUserId, ct); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; } catch (Exception ex) { _logger.LogWarning(ex, "Scheduled sync failed for {User}", user.JellyfinUserId); }
        }
    }
    private static IEnumerable<SpotifyTrackInfo> FilterTracks(IEnumerable<SpotifyTrackInfo> tracks, UserSyncSettings settings) => tracks.Where(track =>
        !settings.ExcludeArtistFilters.Any(f => string.Join(' ', track.Artists).Contains(f, StringComparison.OrdinalIgnoreCase)) &&
        (settings.IncludeArtistFilters.Count == 0 || settings.IncludeArtistFilters.Any(f => string.Join(' ', track.Artists).Contains(f, StringComparison.OrdinalIgnoreCase))));
}
