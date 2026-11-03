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

    public SyncEngine(
        SpotifyApiClient spotify,
        IUserManager users,
        SpotifyAuthService auth,
        JellySpotStore store,
        DownloadQueueService queue,
        ILogger<SyncEngine> logger)
    {
        _spotify = spotify;
        _users = users;
        _auth = auth;
        _store = store;
        _queue = queue;
        _logger = logger;
    }

    public async Task SyncUserAsync(Guid userId, CancellationToken ct = default)
    {
        var settings = await _store.GetUserSettingsAsync(userId, ct).ConfigureAwait(false);
        if (!settings.Enabled || _users.GetUserById(userId) == null || await _auth.GetValidTokensAsync(userId, ct) == null)
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

                await _store.SaveCollectionAsync(new UserCollection
                {
                    OwnerId = userId, SourceId = "liked", Name = "Liked Songs", UpdatedAtUtc = DateTime.UtcNow,
                    Tracks = FilterTracks(liked, settings).ToList()
                }, ct);

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
        if (result == null) throw new InvalidOperationException("Spotify playlist could not be read.");

        var (playlist, tracks) = result.Value;
        if (playlist.ItemsRestricted) throw new InvalidOperationException("Spotify playlist is unavailable for this account.");
        foreach (var track in FilterTracks(tracks, settings))
        {
            await _queue.EnqueueTrackAsync(userId, track, playlist.Id, playlist.Name, ct).ConfigureAwait(false);
        }

        await _store.SaveCollectionAsync(new UserCollection
        {
            OwnerId = userId, SourceId = playlist.Id, Name = playlist.Name, CoverUrl = playlist.ImageUrl,
            UpdatedAtUtc = DateTime.UtcNow, Tracks = FilterTracks(tracks, settings).ToList()
        }, ct);

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

}
