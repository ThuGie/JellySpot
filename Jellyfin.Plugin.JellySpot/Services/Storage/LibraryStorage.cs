using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.JellySpot.Models;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellySpot.Services.Storage;

public partial class LibraryStorage
{
    private readonly JellySpotStore _store;
    private readonly ILibraryManager _libraryManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<LibraryStorage> _logger;

    public LibraryStorage(
        JellySpotStore store,
        ILibraryManager libraryManager,
        IHttpClientFactory httpClientFactory,
        ILogger<LibraryStorage> logger)
    {
        _store = store;
        _libraryManager = libraryManager;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public string GetStorageRoot()
    {
        var root = LibraryCatalog.ResolveStorageRoot(_libraryManager, _logger);
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException(
                "Storage root is not configured. Pick a music library or set a folder under Dashboard → JellySpot.");
        }

        Directory.CreateDirectory(root);
        return root;
    }

    public string BuildRelativePath(SpotifyTrackInfo track, string extension)
    {
        var albumArtist = Sanitize(track.AlbumArtist ?? track.Artists.FirstOrDefault() ?? "Unknown Artist");
        var album = Sanitize(track.Album);
        if (track.Year is > 0)
        {
            album = $"{album} ({track.Year})";
        }

        var trackNo = track.TrackNumber > 0 ? $"{track.TrackNumber:00} - " : string.Empty;
        var title = Sanitize(track.Name);
        var fileName = $"{trackNo}{title}.{extension.TrimStart('.')}";
        return Path.Combine(albumArtist, album, fileName);
    }

    public string GetAbsolutePath(string relativePath)
    {
        return Path.Combine(GetStorageRoot(), relativePath);
    }

    public async Task<bool> TrackFileExistsAsync(string spotifyTrackId, CancellationToken ct = default)
    {
        return await FindExistingRelativePathAsync(spotifyTrackId, null, ct).ConfigureAwait(false) != null;
    }

    public async Task<string?> FindExistingRelativePathAsync(SpotifyTrackInfo track, CancellationToken ct = default)
    {
        return await FindExistingRelativePathAsync(track.Id, track, ct).ConfigureAwait(false);
    }

    public async Task<string?> FindExistingRelativePathAsync(
        string spotifyTrackId,
        SpotifyTrackInfo? track,
        CancellationToken ct = default)
    {
        var entry = await _store.GetTrackIndexAsync(spotifyTrackId, ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(entry?.RelativePath) && FileExistsNonEmpty(GetAbsolutePath(entry.RelativePath)))
        {
            return entry.RelativePath;
        }

        if (track == null)
        {
            return null;
        }

        var preferred = (Plugin.Instance?.Configuration.PreferredFormat ?? "m4a").Trim('.').ToLowerInvariant();
        foreach (var ext in new[] { preferred, "m4a", "mp3", "opus", "ogg" }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var relative = BuildRelativePath(track, ext);
            if (FileExistsNonEmpty(GetAbsolutePath(relative)))
            {
                return relative;
            }
        }

        return null;
    }

    private static bool FileExistsNonEmpty(string path)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task SaveCoverAsync(string albumFolderAbsolute, string? coverUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(coverUrl))
        {
            return;
        }

        var coverPath = Path.Combine(albumFolderAbsolute, "cover.jpg");
        if (File.Exists(coverPath))
        {
            return;
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            var bytes = await client.GetByteArrayAsync(coverUrl, ct).ConfigureAwait(false);
            await File.WriteAllBytesAsync(coverPath, bytes, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to download cover art");
        }
    }

    public async Task WritePlaylistM3u8Async(
        string playlistName,
        IEnumerable<string> relativeTrackPaths,
        CancellationToken ct = default)
    {
        var root = GetStorageRoot();
        var playlistsDir = Path.Combine(root, "Playlists");
        Directory.CreateDirectory(playlistsDir);
        var filePath = Path.Combine(playlistsDir, Sanitize(playlistName) + ".m3u8");

        var sb = new StringBuilder();
        sb.AppendLine("#EXTM3U");
        foreach (var relative in relativeTrackPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var absolute = GetAbsolutePath(relative);
            if (!File.Exists(absolute))
            {
                continue;
            }

            sb.AppendLine($"#EXTINF:-1,{Path.GetFileNameWithoutExtension(relative)}");
            // Relative from playlist file to track
            var rel = Path.GetRelativePath(playlistsDir, absolute).Replace('\\', '/');
            sb.AppendLine(rel);
        }

        await File.WriteAllTextAsync(filePath, sb.ToString(), ct).ConfigureAwait(false);
    }

    public static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Unknown";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        cleaned = MultiSpaceRegex().Replace(cleaned, " ").Trim().Trim('.');
        return string.IsNullOrWhiteSpace(cleaned) ? "Unknown" : cleaned;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex MultiSpaceRegex();
}
