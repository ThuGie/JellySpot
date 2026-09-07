using Jellyfin.Plugin.JellySpot.Services.Sync;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.JellySpot.ScheduledTasks;

public class SyncScheduledTask : IScheduledTask
{
    private readonly SyncEngine _syncEngine;

    public SyncScheduledTask(SyncEngine syncEngine)
    {
        _syncEngine = syncEngine;
    }

    public string Name => "JellySpot Sync";

    public string Key => "JellySpotSync";

    public string Description => "Sync monitored Spotify libraries/playlists and queue missing tracks for download.";

    public string Category => "JellySpot";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        progress.Report(0);
        await _syncEngine.SyncAllEnabledUsersAsync(cancellationToken).ConfigureAwait(false);
        progress.Report(100);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        var minutes = Math.Max(15, Plugin.Instance?.Configuration.SyncIntervalMinutes ?? 60);
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromMinutes(minutes).Ticks
            }
        ];
    }
}
