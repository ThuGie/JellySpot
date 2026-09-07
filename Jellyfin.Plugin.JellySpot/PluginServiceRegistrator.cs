using Jellyfin.Plugin.JellySpot.Services.Download;
using Jellyfin.Plugin.JellySpot.Services.Matching;
using Jellyfin.Plugin.JellySpot.Services.Spotify;
using Jellyfin.Plugin.JellySpot.Services.Storage;
using Jellyfin.Plugin.JellySpot.Services.Sync;
using Jellyfin.Plugin.JellySpot.Services.YouTubeMusic;
using Jellyfin.Plugin.JellySpot.ScheduledTasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.JellySpot;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHttpClient();
        serviceCollection.AddSingleton<JellySpotStore>();
        serviceCollection.AddSingleton<SpotifyRateLimiter>();
        serviceCollection.AddSingleton<SpotifyCache>();
        serviceCollection.AddSingleton<SpotifyAuthService>();
        serviceCollection.AddSingleton<SpotifyApiClient>();
        serviceCollection.AddSingleton<YouTubeMusicClient>();
        serviceCollection.AddSingleton<MatchScorer>();
        serviceCollection.AddSingleton<TrackMatcher>();
        serviceCollection.AddSingleton<TrackDownloader>();
        serviceCollection.AddSingleton<LibraryStorage>();
        serviceCollection.AddSingleton<DownloadQueueService>();
        serviceCollection.AddSingleton<SyncEngine>();
        serviceCollection.AddSingleton<IScheduledTask, SyncScheduledTask>();
        serviceCollection.AddSingleton<IScheduledTask, StartupService>();
    }
}
