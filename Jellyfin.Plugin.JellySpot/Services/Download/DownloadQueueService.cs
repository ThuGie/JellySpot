using Jellyfin.Plugin.JellySpot.Models;
using Jellyfin.Plugin.JellySpot.Services.Storage;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.JellySpot.Services.Download;

public class DownloadQueueService
{
    private readonly ILibraryManager _libraryManager;
    private DateTime _lastScan;
    private bool _scanPending;
    private readonly object _scanGate = new();
    private readonly JellySpotStore _store;
    private readonly LibraryStorage _storage;
    private readonly TrackDownloader _downloader;
    private readonly ILogger<DownloadQueueService> _logger;
    private readonly object _startGate = new();
    private readonly SemaphoreSlim _workerLock = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _worker;

    public DownloadQueueService(
        JellySpotStore store,
        ILibraryManager libraryManager,
        LibraryStorage storage,
        TrackDownloader downloader,
        ILogger<DownloadQueueService> logger)
    {
        _store = store;
        _libraryManager = libraryManager;
        _storage = storage;
        _downloader = downloader;
        _logger = logger;
    }

    public async Task<EnqueueResult> EnqueueTrackAsync(Guid userId, SpotifyTrackInfo track, string? playlistId = null, string? playlistName = null, CancellationToken ct = default, string? operationId = null)
    {
        var owned = await _storage.IsOwnedAsync(track, ct).ConfigureAwait(false);

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
            Status = owned ? "Completed" : "Pending"
        };
        var result = await _store.EnqueueAsync(item, ct, missingAudio: !owned, operationId: operationId).ConfigureAwait(false);
        if (result is EnqueueResult.Added or EnqueueResult.Retried)
        {
            EnsureWorker();
        }

        return result;
    }

    public void EnsureWorker()
    {
        lock (_startGate)
        {
            if (_worker is { IsCompleted: false }) return;
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            _worker = Task.Run(() => ProcessLoopAsync(_cts.Token));
        }
    }

    public void RefreshLibrary()
    {
        lock (_scanGate) _scanPending = true;
        try { FlushLibraryRefresh(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Audio completed; library scan will be retried"); }
    }

    public void FlushLibraryRefresh()
    {
        lock (_scanGate)
        {
            if (!_scanPending) return;
            if (Plugin.Instance?.Configuration.TriggerLibraryRefresh != true || DateTime.UtcNow - _lastScan < TimeSpan.FromSeconds(30)) return;
            if (!Directory.Exists(_storage.GetStorageRoot())) return;
            _libraryManager.QueueLibraryScan();
            _lastScan = DateTime.UtcNow;
            _scanPending = false;
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        Task? worker;
        lock (_startGate) { _cts?.Cancel(); worker = _worker; }
        if (worker != null)
            try { await worker.WaitAsync(ct); } catch (OperationCanceledException) { }
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
                var pending = await _store.EligibleJobsAsync(50, ct).ConfigureAwait(false);
                if (pending.Count == 0)
                {
                    await Task.Delay(1500, ct).ConfigureAwait(false);
                    pending = await _store.EligibleJobsAsync(50, ct).ConfigureAwait(false);
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
                        if (!await _store.ClaimJobAsync(item.Id, ct)) return;
                        await _downloader.DownloadAsync(item, false, ct).ConfigureAwait(false);
                        if (item.Status == "Completed") RefreshLibrary();
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        item.Status = "Pending";
                        item.Error = null;
                        item.Progress = null;
                        await _store.UpdateQueueItemAsync(item, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        item.Status = "Failed";
                        item.Error = "Download failed. See server logs for details.";
                        await _store.UpdateQueueItemAsync(item, CancellationToken.None);
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
