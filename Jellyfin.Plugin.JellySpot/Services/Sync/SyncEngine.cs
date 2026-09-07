using Jellyfin.Plugin.JellySpot.Models;
using Jellyfin.Plugin.JellySpot.Services.Download;
using Jellyfin.Plugin.JellySpot.Services.Spotify;
using Jellyfin.Plugin.JellySpot.Services.Storage;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellySpot.Services.Sync;

public class SyncEngine
{
    private readonly SpotifyApiClient _spotify;
    private readonly JellySpotStore _store;
    private readonly DownloadQueueService _queue;
    private readonly LibraryStorage _storage;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<SyncEngine> _logger;
    private DateTime _lastLibraryRefresh = DateTime.MinValue;

    public SyncEngine(
        SpotifyApiClient spotify,
        JellySpotStore store,
        DownloadQueueService queue,
        LibraryStorage storage,
        ILibraryManager libraryManager,
        ILogger<SyncEngine> logger)
    {
        _spotify = spotify;
        _store = store;
        _queue = queue;
        _storage = storage;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    public async Task SyncUserAsync(Guid userId, CancellationToken ct = default)
    {
        var settings = await _store.GetUserSettingsAsync(userId, ct).ConfigureAwait(false);
        if (!settings.Enabled)
        {
            return;
        }

        try
        {
            if (settings.SyncLikedSongs)
            {
                var liked = await _spotify.GetLikedSongsAsync(userId, ct).ConfigureAwait(false);
                foreach (var track in FilterTracks(liked, settings))
                {
                    await _queue.EnqueueTrackAsync(userId, track, "liked", "Liked Songs", ct).ConfigureAwait(false);
                }

                var paths = new List<string>();
                foreach (var track in liked)
                {
                    var idx = await _store.GetTrackIndexAsync(track.Id, ct).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(idx?.RelativePath))
                    {
                        paths.Add(idx.RelativePath);
                    }
                }

                await _storage.WritePlaylistM3u8Async("Liked Songs", paths, ct).ConfigureAwait(false);
            }

            foreach (var playlistId in settings.MonitoredPlaylistIds.Distinct())
            {
                await SyncPlaylistAsync(userId, playlistId, settings, ct).ConfigureAwait(false);
            }

            foreach (var albumId in settings.MonitoredAlbumIds.Distinct())
            {
                var tracks = await _spotify.GetAlbumTracksAsync(userId, albumId, ct).ConfigureAwait(false);
                foreach (var track in FilterTracks(tracks, settings))
                {
                    await _queue.EnqueueTrackAsync(userId, track, albumId, track.Album, ct).ConfigureAwait(false);
                }
            }

            settings.LastSyncUtc = DateTime.UtcNow;
            settings.LastSyncStatus = "OK";
            settings.LastError = null;
            await _store.SaveUserSettingsAsync(settings, ct).ConfigureAwait(false);

            MaybeRefreshLibrary();
            _queue.EnsureWorker();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sync failed for user {UserId}", userId);
            settings.LastSyncUtc = DateTime.UtcNow;
            settings.LastSyncStatus = "Failed";
            settings.LastError = ex.Message;
            await _store.SaveUserSettingsAsync(settings, ct).ConfigureAwait(false);
            throw;
        }
    }

    public async Task SyncAllEnabledUsersAsync(CancellationToken ct = default)
    {
        var users = await _store.GetAllEnabledUsersAsync(ct).ConfigureAwait(false);
        foreach (var user in users)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await SyncUserAsync(user.JellyfinUserId, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scheduled sync failed for {UserId}", user.JellyfinUserId);
            }
        }
    }

    private async Task SyncPlaylistAsync(Guid userId, string playlistId, UserSyncSettings settings, CancellationToken ct)
    {
        var result = await _spotify.GetPlaylistWithTracksAsync(userId, playlistId, ct).ConfigureAwait(false);
        if (result == null)
        {
            return;
        }

        var (playlist, tracks) = result.Value;
        var existing = await _store.GetPlaylistSnapshotAsync(userId, playlistId, ct).ConfigureAwait(false);
        if (existing != null &&
            !string.IsNullOrEmpty(playlist.SnapshotId) &&
            string.Equals(existing.SnapshotId, playlist.SnapshotId, StringComparison.Ordinal))
        {
            _logger.LogInformation("Playlist {Name} unchanged (snapshot {Snapshot})", playlist.Name, playlist.SnapshotId);
            return;
        }

        foreach (var track in FilterTracks(tracks, settings))
        {
            await _queue.EnqueueTrackAsync(userId, track, playlist.Id, playlist.Name, ct).ConfigureAwait(false);
        }

        var paths = new List<string>();
        foreach (var track in tracks)
        {
            var idx = await _store.GetTrackIndexAsync(track.Id, ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(idx?.RelativePath))
            {
                paths.Add(idx.RelativePath);
            }
        }

        await _storage.WritePlaylistM3u8Async(playlist.Name, paths, ct).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(playlist.SnapshotId))
        {
            await _store.SavePlaylistSnapshotAsync(new PlaylistSnapshot
            {
                PlaylistId = playlistId,
                JellyfinUserId = userId,
                SnapshotId = playlist.SnapshotId,
                Name = playlist.Name
            }, ct).ConfigureAwait(false);
        }
    }

    private static IEnumerable<SpotifyTrackInfo> FilterTracks(IEnumerable<SpotifyTrackInfo> tracks, UserSyncSettings settings)
    {
        foreach (var track in tracks)
        {
            var artistBlob = string.Join(' ', track.Artists).ToLowerInvariant();
            if (settings.ExcludeArtistFilters.Count > 0 &&
                settings.ExcludeArtistFilters.Any(f => artistBlob.Contains(f, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (settings.IncludeArtistFilters.Count > 0 &&
                !settings.IncludeArtistFilters.Any(f => artistBlob.Contains(f, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            yield return track;
        }
    }

    private void MaybeRefreshLibrary()
    {
        if (Plugin.Instance?.Configuration.TriggerLibraryRefresh != true)
        {
            return;
        }

        if (DateTime.UtcNow - _lastLibraryRefresh < TimeSpan.FromMinutes(5))
        {
            return;
        }

        try
        {
            var root = Plugin.Instance.Configuration.StorageRootPath;
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                return;
            }

            _libraryManager.QueueLibraryScan();
            _lastLibraryRefresh = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Library refresh failed");
        }
    }
}
