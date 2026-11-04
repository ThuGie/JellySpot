using Jellyfin.Plugin.JellySpot.Services.Sync;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.JellySpot.ScheduledTasks;

public class SyncScheduledTask : IScheduledTask
{
    private readonly Services.Storage.JellySpotStore _store;
    private readonly MediaBrowser.Controller.Library.IUserManager _users;

    public SyncScheduledTask(Services.Storage.JellySpotStore store, MediaBrowser.Controller.Library.IUserManager users)
    {
        _store=store;_users=users;
    }

    public string Name => "JellySpot Sync";

    public string Key => "JellySpotSync";

    public string Description => "Sync monitored Spotify libraries/playlists and queue missing tracks for download.";

    public string Category => "JellySpot";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        progress.Report(0);
        foreach(var user in await _store.GetAllEnabledUsersAsync(cancellationToken))
        {
            var tokens=await _store.GetTokensAsync(user.JellyfinUserId,cancellationToken);
            if(tokens==null||tokens.RelinkRequired||_users.GetUserById(user.JellyfinUserId)==null)continue;
            await _store.AcceptOperationAsync(user.JellyfinUserId,tokens.Generation,new(){Kind="sync"},cancellationToken);
        }
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
