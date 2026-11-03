using Jellyfin.Plugin.JellySpot.Services.Download;
using Jellyfin.Plugin.JellySpot.Services.Storage;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellySpot.Services;

/// <summary>Resumes persisted work and performs deferred library refreshes independently of Spotify.</summary>
public sealed class QueueLifecycleService : BackgroundService
{
    private readonly DownloadQueueService _queue;
    private readonly JellySpotStore _store;
    private readonly ILibraryManager _libraries;
    private readonly ILogger<QueueLifecycleService> _logger;
    public QueueLifecycleService(DownloadQueueService queue, JellySpotStore store, ILibraryManager libraries, ILogger<QueueLifecycleService> logger)
    { _queue = queue; _store = store; _libraries = libraries; _logger = logger; }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            ReviewLegacyExports();
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if ((await _store.GetQueueAsync("Pending", 1, stoppingToken)).Count > 0) _queue.EnsureWorker();
                    _queue.FlushLibraryRefresh();
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogError(ex, "JellySpot queue maintenance failed; will retry"); }
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
    private void ReviewLegacyExports()
    {
        try
        {
            var root = LibraryCatalog.ResolveStorageRoot(_libraries, _logger);
            if (string.IsNullOrWhiteSpace(root)) return;
            var directory = Path.Combine(root, "Playlists");
            if (!Directory.Exists(directory)) return;
            // Legacy releases wrote no generator marker. Names/EXTM3U headers do not establish ownership.
            var ambiguous = Directory.EnumerateFiles(directory, "*.m3u8", SearchOption.TopDirectoryOnly).ToArray();
            if (ambiguous.Length == 0) return;
            var report = Plugin.Instance!.GetDataPath("legacy-playlist-review.txt");
            File.WriteAllLines(report, new[] { "Review these legacy exports: ownership cannot be established automatically. They may expose personal playlist membership through Jellyfin." }.Concat(ambiguous));
            _logger.LogWarning("Legacy playlist exports may expose personal membership. Review {Report}", report);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not review legacy playlist exports"); }
    }
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await _queue.StopAsync(cancellationToken);
    }
}
