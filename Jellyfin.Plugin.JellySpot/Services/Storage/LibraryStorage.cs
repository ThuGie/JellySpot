using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.JellySpot.Models;
using Jellyfin.Plugin.JellySpot.Services.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellySpot.Services.Storage;

public partial class LibraryStorage
{
    private readonly JellySpotStore _store;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<LibraryStorage> _logger;

    public LibraryStorage(
        JellySpotStore store,
        IHttpClientFactory httpClientFactory,
        ILogger<LibraryStorage> logger)
    {
        _store = store;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public string GetStorageRoot()
    {
        var root = Plugin.Instance?.Configuration.StorageRootPath;
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException("Storage root path is not configured in JellySpot admin settings.");
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
        var entry = await _store.GetTrackIndexAsync(spotifyTrackId, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(entry?.RelativePath))
        {
            return false;
        }

        return File.Exists(GetAbsolutePath(entry.RelativePath));
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
