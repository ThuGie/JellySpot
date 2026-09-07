using Jellyfin.Plugin.JellySpot.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.JellySpot;

public class Plugin : BasePlugin<PluginConfiguration>, IHasPluginConfiguration, IHasWebPages
{
    public const string PluginGuid = "a7c3e8f1-9b2d-4e5a-8f6c-1d2e3f4a5b6c";

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public override string Name => "JellySpot";

    public override Guid Id => Guid.Parse(PluginGuid);

    public override string Description =>
        "Sync Spotify libraries and playlists to a local music folder via YouTube Music matching.";

    public static Plugin? Instance { get; private set; }

    public IEnumerable<PluginPageInfo> GetPages()
    {
        // IMPORTANT: Only ONE EnableInMainMenu page per plugin.
        // Jellyfin web uses PluginId as the React list key in PluginDrawerSection,
        // so multiple EnableInMainMenu pages from the same plugin do not appear.
        yield return new PluginPageInfo
        {
            Name = Name,
            DisplayName = "JellySpot",
            EnableInMainMenu = true,
            MenuIcon = "music_note",
            EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.config.html"
        };
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
}
