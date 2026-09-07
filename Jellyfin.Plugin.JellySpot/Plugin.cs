using Jellyfin.Plugin.JellySpot.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.JellySpot;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public override string Name => "JellySpot";

    public override Guid Id => Guid.Parse("a7c3e8f1-9b2d-4e5a-8f6c-1d2e3f4a5b6c");

    public override string Description =>
        "Sync Spotify libraries and playlists to a local music folder via YouTube Music matching.";

    public static Plugin? Instance { get; private set; }

    public IEnumerable<PluginPageInfo> GetPages()
    {
        var assemblyPrefix = GetType().Namespace;

        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = $"{assemblyPrefix}.Web.config.html"
            },
            new PluginPageInfo
            {
                Name = "JellySpotConfigJs",
                EmbeddedResourcePath = $"{assemblyPrefix}.Web.config.js"
            },
            new PluginPageInfo
            {
                Name = "JellySpotBrowse",
                DisplayName = "JellySpot Browse",
                EnableInMainMenu = true,
                EmbeddedResourcePath = $"{assemblyPrefix}.Web.browse.html"
            },
            new PluginPageInfo
            {
                Name = "JellySpotBrowseJs",
                EmbeddedResourcePath = $"{assemblyPrefix}.Web.browse.js"
            },
            new PluginPageInfo
            {
                Name = "JellySpotSync",
                DisplayName = "JellySpot Sync",
                EnableInMainMenu = true,
                EmbeddedResourcePath = $"{assemblyPrefix}.Web.sync.html"
            },
            new PluginPageInfo
            {
                Name = "JellySpotSyncJs",
                EmbeddedResourcePath = $"{assemblyPrefix}.Web.sync.js"
            },
            new PluginPageInfo
            {
                Name = "JellySpotQueue",
                DisplayName = "JellySpot Queue",
                EnableInMainMenu = true,
                EmbeddedResourcePath = $"{assemblyPrefix}.Web.queue.html"
            },
            new PluginPageInfo
            {
                Name = "JellySpotQueueJs",
                EmbeddedResourcePath = $"{assemblyPrefix}.Web.queue.js"
            },
            new PluginPageInfo
            {
                Name = "JellySpotCss",
                EmbeddedResourcePath = $"{assemblyPrefix}.Web.jellyspot.css"
            }
        ];
    }

    public string GetDataPath(string relative)
    {
        var path = Path.Combine(DataFolderPath, relative);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        return path;
    }

    public string FormatVersion()
    {
        return Version.ToString(4);
    }
}
