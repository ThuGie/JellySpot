using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.JellySpot.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public string StorageRootPath { get; set; } = string.Empty;

    public string SpotifyClientId { get; set; } = string.Empty;

    public string SpotifyClientSecret { get; set; } = string.Empty;

    public string SpotifyRedirectUri { get; set; } = "http://127.0.0.1:8096/JellySpot/OAuth/Callback";

    public double SpotifyRequestsPerSecond { get; set; } = 2.0;

    public int SpotifyMaxConcurrentRequests { get; set; } = 2;

    public int DownloadConcurrency { get; set; } = 2;

    public string PreferredFormat { get; set; } = "m4a";

    public double MinMatchScore { get; set; } = 80.0;

    public int SyncIntervalMinutes { get; set; } = 60;

    public bool TriggerLibraryRefresh { get; set; } = true;

    public int CacheTtlDays { get; set; } = 10;

    public string FfmpegPath { get; set; } = string.Empty;
}
