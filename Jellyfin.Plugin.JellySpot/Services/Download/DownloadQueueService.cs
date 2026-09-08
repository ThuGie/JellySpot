using Jellyfin.Plugin.JellySpot.Models;
using Jellyfin.Plugin.JellySpot.Services.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellySpot.Services.Download;

public class DownloadQueueService
{
    private readonly JellySpotStore _store;
    private readonly LibraryStorage _storage;
    private readonly TrackDownloader _downloader;
    private readonly ILogger<DownloadQueueService> _logger;
    private readonly SemaphoreSlim _workerLock = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _worker;

    public DownloadQueueService(
        JellySpotStore store,
        LibraryStorage storage,
        TrackDownloader downloader,
        ILogger<DownloadQueueService> logger)
    {
        _store = store;
        _storage = storage;
        _downloader = downloader;
        _logger = logger;
    }

    public async Task<EnqueueResult> EnqueueTrackAsync(Guid userId, SpotifyTrackInfo track, string? playlistId = null, string? playlistName = null, CancellationToken ct = default)
    {
        if (await _storage.IsOwnedAsync(track, ct).ConfigureAwait(false))
        {
            return EnqueueResult.AlreadyDownloaded;
        }

        var item = new QueueItem
        {
            JellyfinUserId = userId,
            SpotifyTrackId = track.Id,
            Title = track.Name,
            Artists = string.Join(", ", track.Artists),
            Album = track.Album,
            AlbumArtist = track.AlbumArtist,
            Isrc = track.Isrc,
            DurationMs = track.DurationMs,
            TrackNumber = track.TrackNumber,
            DiscNumber = track.DiscNumber,
            Year = track.Year,
            CoverUrl = track.CoverUrl,
            PlaylistId = playlistId,
            PlaylistName = playlistName,
            Status = "Pending"
        };
        var result = await _store.EnqueueAsync(item, ct).ConfigureAwait(false);
        if (result is EnqueueResult.Added or EnqueueResult.Retried)
        {
            EnsureWorker();
        }

        return result;
    }

    public void EnsureWorker()
    {
        if (_worker is { IsCompleted: false })
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _worker = Task.Run(() => ProcessLoopAsync(_cts.Token));
    }

    public async Task RematchAsync(string queueItemId, CancellationToken ct = default)
    {
        var item = await _store.GetQueueItemAsync(queueItemId, ct).ConfigureAwait(false);
        if (item == null)
        {
            return;
        }

        await _store.ClearTrackMatchAsync(item.SpotifyTrackId, ct).ConfigureAwait(false);
        item.Status = "Pending";
        item.Error = null;
        item.YoutubeVideoId = null;
        item.MatchScore = null;
        await _store.UpdateQueueItemAsync(item, ct).ConfigureAwait(false);
        EnsureWorker();
    }

    public async Task RetryAsync(string queueItemId, CancellationToken ct = default)
    {
        var item = await _store.GetQueueItemAsync(queueItemId, ct).ConfigureAwait(false);
        if (item == null)
        {
            return;
        }

        item.Status = "Pending";
        item.Error = null;
        await _store.UpdateQueueItemAsync(item, ct).ConfigureAwait(false);
        EnsureWorker();
    }

    public async Task<int> RetryFailedAsync(CancellationToken ct = default)
    {
        var failed = await _store.GetQueueAsync("Failed", 500, ct).ConfigureAwait(false);
        foreach (var item in failed)
        {
            item.Status = "Pending";
            item.Error = null;
            await _store.UpdateQueueItemAsync(item, ct).ConfigureAwait(false);
        }

        if (failed.Count > 0)
        {
            EnsureWorker();
        }

        return failed.Count;
    }

    private async Task ProcessLoopAsync(CancellationToken ct)
    {
        if (!await _workerLock.WaitAsync(0, ct).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var concurrency = Math.Max(1, Plugin.Instance?.Configuration.DownloadConcurrency ?? 2);
            using var gate = new SemaphoreSlim(concurrency, concurrency);

            while (!ct.IsCancellationRequested)
            {
                var pending = await _store.GetQueueAsync("Pending", 50, ct).ConfigureAwait(false);
                if (pending.Count == 0)
                {
                    await Task.Delay(1500, ct).ConfigureAwait(false);
                    pending = await _store.GetQueueAsync("Pending", 50, ct).ConfigureAwait(false);
                    if (pending.Count == 0)
                    {
                        break;
                    }
                }

                var tasks = pending.Select(async item =>
                {
                    await gate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        await _downloader.DownloadAsync(item, false, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Queue item {Id} failed", item.Id);
                    }
                    finally
                    {
                        gate.Release();
                    }
                });

                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
        }
        finally
        {
            _workerLock.Release();
        }
    }
}
